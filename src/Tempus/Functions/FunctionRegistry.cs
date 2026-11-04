using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Tempus.Functions;

/// <summary>Invokes a registered function. Returns the result object (or null) and whether a result was returned.</summary>
public delegate Task<(bool HasResult, object? Result, Type? ResultType)> TickerFunctionInvoker(IServiceProvider scopedServices, TickerFunctionContext context, CancellationToken cancellationToken);

/// <summary>Everything the runtime knows about one registered function.</summary>
public sealed class TickerFunctionDescriptor
{
    public required string Name { get; init; }
    public Type? RequestType { get; set; }
    public Type? ResultType { get; set; }
    public int MaxConcurrency { get; set; }
    public TickerTaskPriority Priority { get; set; } = TickerTaskPriority.Normal;
    public string? CronExpression { get; set; }
    public string? Source { get; set; }
    public Type? ImplementationType { get; set; }
    public required TickerFunctionInvoker Invoker { get; init; }

    internal SemaphoreSlim? Semaphore { get; set; }
}

/// <summary>Fluent options applied when mapping a function.</summary>
public sealed class TickerFunctionBuilder
{
    internal TickerFunctionDescriptor Descriptor { get; }
    internal TickerFunctionBuilder(TickerFunctionDescriptor descriptor) => Descriptor = descriptor;

    /// <summary>Per-host concurrency limit (0 = unlimited). Not a distributed lock.</summary>
    public TickerFunctionBuilder WithMaxConcurrency(int maxConcurrency) { Descriptor.MaxConcurrency = Math.Max(0, maxConcurrency); return this; }
    public TickerFunctionBuilder WithPriority(TickerTaskPriority priority) { Descriptor.Priority = priority; return this; }
    /// <summary>Seed a code-owned cron ticker for this function.</summary>
    public TickerFunctionBuilder WithCron(string expression) { Descriptor.CronExpression = expression; return this; }
    public TickerFunctionBuilder WithResultType<TResult>() { Descriptor.ResultType = typeof(TResult); return this; }
}

/// <summary>A registration record collected from DI at startup.</summary>
public sealed class TickerFunctionRegistration
{
    public TickerFunctionRegistration(TickerFunctionDescriptor descriptor) => Descriptor = descriptor;
    public TickerFunctionDescriptor Descriptor { get; }
}

/// <summary>Immutable registry of every function on this host.</summary>
public sealed class TickerFunctionRegistry
{
    private readonly ConcurrentDictionary<string, TickerFunctionDescriptor> _functions = new(StringComparer.Ordinal);

    public TickerFunctionRegistry(IEnumerable<TickerFunctionRegistration> registrations)
    {
        foreach (var reg in registrations)
        {
            var d = reg.Descriptor;
            if (!_functions.TryAdd(d.Name, d))
                throw new TempusException($"Function '{d.Name}' is registered more than once. Use a unique, stable function name.");
            if (d.MaxConcurrency > 0) d.Semaphore = new SemaphoreSlim(d.MaxConcurrency, d.MaxConcurrency);
        }
    }

    public IReadOnlyCollection<TickerFunctionDescriptor> Functions => _functions.Values.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();

    public bool TryGet(string name, out TickerFunctionDescriptor descriptor) => _functions.TryGetValue(name, out descriptor!);

    public TickerFunctionDescriptor Get(string name) =>
        _functions.TryGetValue(name, out var d) ? d : throw new TempusException($"Function '{name}' is not registered on this host.");

    public bool Contains(string name) => _functions.ContainsKey(name);
}

/// <summary>Builds invokers for classes, delegates and attribute-discovered methods.</summary>
public static class FunctionInvokerFactory
{
    public static TickerFunctionInvoker ForClass<TFunction>() where TFunction : class, ITickerFunction =>
        (sp, ctx, ct) => InvokeTask(() => ActivatorUtilities.GetServiceOrCreateInstance<TFunction>(sp).ExecuteAsync(ctx, ct), ctx);

    public static TickerFunctionInvoker ForClass<TFunction, TRequest>() where TFunction : class, ITickerFunction<TRequest> =>
        (sp, ctx, ct) => InvokeTask(() => ActivatorUtilities.GetServiceOrCreateInstance<TFunction>(sp).ExecuteAsync(new TickerFunctionContext<TRequest>(ctx), ct), ctx);

    public static TickerFunctionInvoker ForClass<TFunction, TRequest, TResult>() where TFunction : class, ITickerFunction<TRequest, TResult> =>
        async (sp, ctx, ct) =>
        {
            var result = await ActivatorUtilities.GetServiceOrCreateInstance<TFunction>(sp).ExecuteAsync(new TickerFunctionContext<TRequest>(ctx), ct).ConfigureAwait(false);
            return (true, result, typeof(TResult));
        };

    public static TickerFunctionInvoker ForDelegate(Func<TickerFunctionContext, CancellationToken, Task> handler) =>
        (sp, ctx, ct) => InvokeTask(() => handler(ctx, ct), ctx);

    public static TickerFunctionInvoker ForDelegate(Func<TickerFunctionContext, IServiceProvider, CancellationToken, Task> handler) =>
        (sp, ctx, ct) => InvokeTask(() => handler(ctx, sp, ct), ctx);

    public static TickerFunctionInvoker ForDelegate<TRequest>(Func<TickerFunctionContext<TRequest>, CancellationToken, Task> handler) =>
        (sp, ctx, ct) => InvokeTask(() => handler(new TickerFunctionContext<TRequest>(ctx), ct), ctx);

    public static TickerFunctionInvoker ForDelegate<TRequest>(Func<TickerFunctionContext<TRequest>, IServiceProvider, CancellationToken, Task> handler) =>
        (sp, ctx, ct) => InvokeTask(() => handler(new TickerFunctionContext<TRequest>(ctx), sp, ct), ctx);

    private static async Task<(bool, object?, Type?)> InvokeTask(Func<Task> run, TickerFunctionContext ctx)
    {
        await run().ConfigureAwait(false);
        return ctx.ResultSet ? (true, null, null) : (false, null, null);
    }

    /// <summary>Build a descriptor for a method marked with <see cref="TickerFunctionAttribute"/>.</summary>
    public static TickerFunctionDescriptor ForMethod(MethodInfo method, TickerFunctionAttribute attribute)
    {
        var parameters = method.GetParameters();
        Type? requestType = null;
        foreach (var p in parameters)
        {
            if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(TickerFunctionContext<>))
                requestType = p.ParameterType.GetGenericArguments()[0];
        }

        var returnType = method.ReturnType;
        Type? resultType = attribute.ResultType;
        Func<object?, Task<(bool, object?, Type?)>> awaiter;
        if (returnType == typeof(void))
            awaiter = _ => Task.FromResult<(bool, object?, Type?)>((false, null, null));
        else if (returnType == typeof(Task))
            awaiter = async r => { await ((Task)r!).ConfigureAwait(false); return (false, null, null); };
        else if (returnType == typeof(ValueTask))
            awaiter = async r => { await ((ValueTask)r!).ConfigureAwait(false); return (false, null, null); };
        else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var t = returnType.GetGenericArguments()[0];
            resultType ??= t;
            awaiter = async r =>
            {
                var task = (Task)r!;
                await task.ConfigureAwait(false);
                var value = task.GetType().GetProperty("Result")!.GetValue(task);
                return (true, value, t);
            };
        }
        else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var t = returnType.GetGenericArguments()[0];
            resultType ??= t;
            awaiter = async r =>
            {
                var task = (Task)returnType.GetMethod("AsTask")!.Invoke(r, null)!;
                await task.ConfigureAwait(false);
                var value = task.GetType().GetProperty("Result")!.GetValue(task);
                return (true, value, t);
            };
        }
        else
        {
            resultType ??= returnType;
            var t = returnType;
            awaiter = r => Task.FromResult<(bool, object?, Type?)>((true, r, t));
        }

        var declaringType = method.DeclaringType!;
        var isStatic = method.IsStatic;
        var ctorInfo = isStatic ? null : SelectConstructor(declaringType);

        TickerFunctionInvoker invoker = async (sp, ctx, ct) =>
        {
            object? instance = null;
            if (!isStatic)
            {
                instance = sp.GetService(declaringType);
                if (instance is null)
                {
                    instance = ctorInfo is null
                        ? ActivatorUtilities.CreateInstance(sp, declaringType)
                        : ctorInfo.Invoke(ctorInfo.GetParameters().Select(p => p.ParameterType == typeof(IServiceProvider) ? sp : sp.GetRequiredService(p.ParameterType)).ToArray());
                }
            }

            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var pt = parameters[i].ParameterType;
                if (pt == typeof(TickerFunctionContext)) args[i] = ctx;
                else if (pt == typeof(CancellationToken)) args[i] = ct;
                else if (pt == typeof(IServiceProvider)) args[i] = sp;
                else if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(TickerFunctionContext<>))
                    args[i] = Activator.CreateInstance(pt, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, [ctx], null);
                else args[i] = sp.GetService(pt) ?? throw new TempusException($"Cannot bind parameter '{parameters[i].Name}' of function '{attribute.FunctionName}'.");
            }

            object? raw;
            try
            {
                raw = method.Invoke(instance, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
            var (hasResult, value, type) = await awaiter(raw).ConfigureAwait(false);
            if (!hasResult && ctx.ResultSet) return (true, null, null);
            return (hasResult, value, type);
        };

        return new TickerFunctionDescriptor
        {
            Name = attribute.FunctionName,
            RequestType = requestType,
            ResultType = resultType,
            MaxConcurrency = attribute.MaxConcurrency,
            Priority = attribute.Priority,
            CronExpression = attribute.CronExpression,
            Source = $"{declaringType.FullName}.{method.Name}",
            ImplementationType = declaringType,
            Invoker = invoker,
        };
    }

    private static ConstructorInfo? SelectConstructor(Type type)
    {
        var ctors = type.GetConstructors();
        if (ctors.Length <= 1) return null;
        return ctors.FirstOrDefault(c => c.GetCustomAttribute<TempusConstructorAttribute>() is not null);
    }
}
