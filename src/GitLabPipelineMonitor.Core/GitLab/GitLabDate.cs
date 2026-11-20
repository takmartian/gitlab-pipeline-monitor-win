using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>GitLab 的日期字段：ISO8601（可能带小数秒），个别字段是 Unix 秒数。</summary>
public static class GitLabDate
{
    /// <summary>宽松解析：解析不出来返回 null，不抛异常。</summary>
    public static DateTime? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        // 带小数秒：2024-01-02T03:04:05.678Z
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var value))
            return value;
        return null;
    }

    /// <summary>从 JSON 元素取日期：字符串按 ISO 解析，数字按 Unix 秒处理。</summary>
    public static DateTime? FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String: return Parse(element.GetString());
            case JsonValueKind.Number when element.TryGetDouble(out var seconds):
                return DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime;
            default: return null;
        }
    }
}

/// <summary>可空的日期时间转换器：类型不符时给 null 而不是让整次解码失败。</summary>
public sealed class GitLabDateConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();
                return GitLabDate.Parse(text);
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var seconds)) return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                if (reader.TryGetDouble(out var d)) return DateTimeOffset.FromUnixTimeSeconds((long)d).UtcDateTime;
                return null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is { } when_) writer.WriteStringValue(value.Value.ToString("o"));
        else writer.WriteNullValue();
    }
}
