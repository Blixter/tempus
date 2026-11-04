using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tempus.Entities;

namespace Tempus.Cosmos;

/// <summary>Document wrapper: <c>{ id, pk, type, data, ttl }</c>. The partition key is the application namespace.</summary>
internal sealed class CosmosDocument<T>
{
    public string Id { get; set; } = string.Empty;
    public string Pk { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public T Data { get; set; } = default!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ttl { get; set; }

    [JsonPropertyName("_etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; set; }
}

/// <summary>Marker that makes planned cron occurrences unique per cron and slot.</summary>
internal sealed class OccurrenceMarker
{
    public Guid CronTickerId { get; set; }
    public Guid OccurrenceId { get; set; }
}

internal static class DocTypes
{
    public const string Time = "time";
    public const string Cron = "cron";
    public const string Occurrence = "occ";
    public const string OccurrenceMarker = "occkey";
    public const string Policy = "policy";
    public const string Node = "node";
    public const string Command = "cmd";
    public const string Log = "log";
    public const string Meta = "meta";
}

internal static class CosmosJson
{
    /// <summary>
    /// camelCase, nulls written (so <c>IS_NULL</c> works), enums as numbers, and fixed-width UTC timestamps so that
    /// string comparison in Cosmos SQL orders the same way as <see cref="DateTime"/>.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveComputedMembers } },
        };
        options.Converters.Add(new UtcDateTimeConverter());
        return options;
    }

    private static void RemoveComputedMembers(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        string[]? drop = null;
        if (typeof(TickerExecutionEntity).IsAssignableFrom(info.Type)) drop = ["tickerType", "isRoot", "children"];
        else if (info.Type == typeof(CronTickerEntity)) drop = ["chain"];
        if (drop is null) return;
        for (var i = info.Properties.Count - 1; i >= 0; i--)
            if (drop.Contains(info.Properties[i].Name)) info.Properties.RemoveAt(i);
    }

    public const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string FormatDate(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        return utc.ToString(DateFormat, CultureInfo.InvariantCulture);
    }

    public static string PropertyName(string memberName) => JsonNamingPolicy.CamelCase.ConvertName(memberName);

    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatDate(value));
    }
}
