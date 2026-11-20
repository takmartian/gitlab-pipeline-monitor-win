using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>
/// 所有模型都按 GitLab 的 JSON 原样 key 取值（JsonPropertyName），
/// 不做 snake_case 自动转换 —— 和 macOS 版同样的理由：自动转换会把 web_url 变成 webUrl，
/// 于是这些字段全部静默取不到，链接拼错、时间显示成几千年前。
/// 缺失字段留默认值而不是让整次解码失败：自建 GitLab 的版本差异很大，宁可少一个字段也不要整页打不开。
/// </summary>
public sealed class GitLabUser
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "?";
    [JsonPropertyName("name")] public string Name { get; set; } = "?";
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("web_url")] public string? WebUrl { get; set; }

    /// <summary>显示名：name 缺失时退回 username，和 macOS 版一致。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Username : Name;

    public static GitLabUser FromJson(System.Text.Json.JsonElement root)
        => new()
        {
            Id = GitLabJson.Int(root, "id") ?? 0,
            Username = GitLabJson.Str(root, "username") ?? "?",
            // name 缺失时退回 username，和 macOS 版一致
            Name = GitLabJson.Str(root, "name") ?? GitLabJson.Str(root, "username") ?? "?",
            AvatarUrl = GitLabJson.Str(root, "avatar_url"),
            WebUrl = GitLabJson.Str(root, "web_url")
        };
}

public static class GitLabJson
{
    public static string? Str(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            // 个别字段（比如 name）在少数版本上是对象/数字，取原文而不是报错
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    public static int? Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var s)) return s;
        return null;
    }

    public static double? Double(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)) return d;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static bool? Bool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static DateTime? Date(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return GitLabDate.FromJson(value);
    }

    public static JsonElement? Object(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        return value;
    }
}
