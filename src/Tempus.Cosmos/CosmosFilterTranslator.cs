using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Azure.Cosmos;

namespace Tempus.Cosmos;

/// <summary>
/// Translates the simple predicates used with the managers (<c>x =&gt; x.Function == name &amp;&amp; x.Status == TickerStatus.Idle</c>)
/// into parameterized Cosmos SQL over <c>c.data</c>. Returns false for anything it does not understand; callers then filter in memory.
/// </summary>
internal sealed class CosmosFilterTranslator
{
    private readonly List<(string Name, object? Value)> _parameters = [];
    private readonly string _prefix;
    private ParameterExpression _parameter = null!;

    private CosmosFilterTranslator(string prefix) => _prefix = prefix;

    public static bool TryTranslate<T>(Expression<Func<T, bool>> filter, string parameterPrefix, out string sql, out IReadOnlyList<(string Name, object? Value)> parameters)
    {
        var t = new CosmosFilterTranslator(parameterPrefix) { _parameter = filter.Parameters[0] };
        try
        {
            sql = t.Predicate(filter.Body);
            parameters = t._parameters;
            return true;
        }
        catch (NotSupportedException)
        {
            sql = string.Empty;
            parameters = [];
            return false;
        }
    }

    public static QueryDefinition Apply(QueryDefinition query, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters) query = query.WithParameter(name, value);
        return query;
    }

    /// <summary>Converts a CLR value to what is stored in the document (dates as fixed-width strings, enums as numbers, guids as strings).</summary>
    public static object? ToStored(object? value) => value switch
    {
        null => null,
        DateTime d => CosmosJson.FormatDate(d),
        DateTimeOffset o => CosmosJson.FormatDate(o.UtcDateTime),
        Guid g => g.ToString("D"),
        Enum e => Convert.ToInt64(e),
        string s => s,
        IEnumerable list => list.Cast<object?>().Select(ToStored).ToArray(),
        _ => value,
    };

    private static Expression StripConvert(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u) e = u.Operand;
        return e;
    }

    private bool IsDocumentMember(Expression e)
    {
        e = StripConvert(e);
        if (e is MemberExpression { Member.Name: "Value" } nv && Nullable.GetUnderlyingType(nv.Expression!.Type) is not null) e = nv.Expression!;
        return e is MemberExpression { Expression: ParameterExpression p } && p == _parameter;
    }

    private string Path(Expression e)
    {
        e = StripConvert(e);
        if (e is MemberExpression { Member.Name: "Value" } nv && Nullable.GetUnderlyingType(nv.Expression!.Type) is not null) e = nv.Expression!;
        if (e is MemberExpression { Expression: ParameterExpression p } m && p == _parameter)
            return $"c[\"data\"][\"{CosmosJson.PropertyName(m.Member.Name)}\"]";
        if (e is BinaryExpression { NodeType: ExpressionType.Coalesce } co && IsDocumentMember(co.Left) && IsDocumentMember(co.Right))
        {
            var l = Path(co.Left);
            return $"((NOT IS_DEFINED({l}) OR IS_NULL({l})) ? {Path(co.Right)} : {l})";
        }
        throw new NotSupportedException();
    }

    private bool IsValue(Expression e) => !References(e);

    private bool References(Expression e)
    {
        var finder = new ParameterFinder(_parameter);
        finder.Visit(e);
        return finder.Found;
    }

    private static object? Evaluate(Expression e) => Expression.Lambda(e).Compile().DynamicInvoke();

    private string Param(object? value)
    {
        var name = $"@{_prefix}{_parameters.Count}";
        _parameters.Add((name, ToStored(value)));
        return name;
    }

    private bool IsMemberLike(Expression e) => IsDocumentMember(e) || StripConvert(e) is BinaryExpression { NodeType: ExpressionType.Coalesce };

    private string Predicate(Expression e)
    {
        switch (e)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } b:
                return $"({Predicate(b.Left)} AND {Predicate(b.Right)})";
            case BinaryExpression { NodeType: ExpressionType.OrElse } b:
                return $"({Predicate(b.Left)} OR {Predicate(b.Right)})";
            case UnaryExpression { NodeType: ExpressionType.Not } u:
                return $"(NOT {Predicate(u.Operand)})";
            case BinaryExpression b when b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan
                or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual:
                return Comparison(b);
            case MemberExpression m when m.Type == typeof(bool) && IsDocumentMember(m):
                return $"({Path(m)} = true)";
            case MemberExpression { Member.Name: "HasValue" } hv when IsDocumentMember(hv.Expression!):
                return NotNull(Path(hv.Expression!));
            case MethodCallExpression call:
                return Method(call);
            case ConstantExpression { Value: bool constant }:
                return constant ? "true" : "false";
        }
        if (e.Type == typeof(bool) && IsValue(e)) return (bool)Evaluate(e)! ? "true" : "false";
        throw new NotSupportedException();
    }

    private static string IsNullOrMissing(string path) => $"(NOT IS_DEFINED({path}) OR IS_NULL({path}))";
    private static string NotNull(string path) => $"(IS_DEFINED({path}) AND NOT IS_NULL({path}))";

    private string Comparison(BinaryExpression b)
    {
        Expression member, value;
        var op = b.NodeType;
        if (IsMemberLike(b.Left) && IsValue(b.Right)) { member = b.Left; value = b.Right; }
        else if (IsMemberLike(b.Right) && IsValue(b.Left)) { member = b.Right; value = b.Left; op = Flip(op); }
        else if (IsMemberLike(b.Left) && IsMemberLike(b.Right)) return $"({Path(b.Left)} {Op(op)} {Path(b.Right)})";
        else throw new NotSupportedException();

        var path = Path(member);
        var v = Evaluate(value);
        if (v is null)
        {
            return op switch
            {
                ExpressionType.Equal => IsNullOrMissing(path),
                ExpressionType.NotEqual => NotNull(path),
                _ => "false",
            };
        }
        var p = Param(v);
        // C#: null != value is true; Cosmos: null != value is undefined. Keep C# semantics.
        return op == ExpressionType.NotEqual ? $"({IsNullOrMissing(path)} OR {path} != {p})" : $"({path} {Op(op)} {p})";
    }

    private static ExpressionType Flip(ExpressionType op) => op switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => op,
    };

    private static string Op(ExpressionType op) => op switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "!=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        _ => throw new NotSupportedException(),
    };

    private string Method(MethodCallExpression call)
    {
        // member.Contains/StartsWith/EndsWith("x")
        if (call.Object is not null && call.Object.Type == typeof(string) && IsDocumentMember(call.Object) && call.Arguments.Count == 1 && IsValue(call.Arguments[0]))
        {
            var fn = call.Method.Name switch { "Contains" => "CONTAINS", "StartsWith" => "STARTSWITH", "EndsWith" => "ENDSWITH", _ => throw new NotSupportedException() };
            return $"{fn}({Path(call.Object)}, {Param(Evaluate(call.Arguments[0]))})";
        }

        // list.Contains(member) or Enumerable.Contains(list, member)
        if (call.Method.Name == "Contains")
        {
            Expression? list = null, item = null;
            if (call.Object is not null && call.Arguments.Count == 1) { list = call.Object; item = call.Arguments[0]; }
            else if (call.Object is null && call.Arguments.Count == 2 && call.Method.DeclaringType == typeof(Enumerable)) { list = call.Arguments[0]; item = call.Arguments[1]; }
            if (list is not null && item is not null && IsValue(list) && IsDocumentMember(item))
                return $"ARRAY_CONTAINS({Param(Evaluate(list))}, {Path(item)})";
        }
        throw new NotSupportedException();
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter) Found = true;
            return node;
        }
    }
}
