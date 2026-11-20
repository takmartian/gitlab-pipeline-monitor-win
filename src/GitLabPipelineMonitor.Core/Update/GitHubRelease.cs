using System.Text.Json;

namespace GitLabPipelineMonitor.Core.Update;

/// <summary>GitHub Release（只取更新用得到的字段）。</summary>
public sealed class GitHubRelease
{
    public string TagName { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public string? Body { get; init; }
    public bool Prerelease { get; init; }
    public IReadOnlyList<Asset> Assets { get; init; } = Array.Empty<Asset>();

    public string Version => AppVersion.Normalize(TagName);

    /// <summary>优先用 zip：解压就是文件，不用挂载 dmg。</summary>
    public Asset? PreferredAsset
    {
        get
        {
            // 直接用索引而不是 LINQ：Assets 是带索引的列表
            Asset? first = null;
            foreach (var asset in Assets)
            {
                first ??= asset;
                if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return asset;
            }
            return first;
        }
    }

    public sealed class Asset
    {
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public string BrowserDownloadUrl { get; init; } = "";

        /// <summary>GitHub 现在会给出 sha256 摘要（"sha256:…"），用来校验下载完整性。</summary>
        public string? Digest { get; init; }
    }

    /// <summary>宽松解析：字段缺失或类型不符都不抛。</summary>
    public static GitHubRelease FromJson(JsonElement root)
    {
        var assets = new List<Asset>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                assets.Add(new Asset
                {
                    Name = GitLab.GitLabJson.Str(item, "name") ?? "",
                    Size = (long)(GitLab.GitLabJson.Double(item, "size") ?? 0),
                    BrowserDownloadUrl = GitLab.GitLabJson.Str(item, "browser_download_url") ?? "",
                    Digest = GitLab.GitLabJson.Str(item, "digest")
                });
            }
        }
        return new GitHubRelease
        {
            TagName = GitLab.GitLabJson.Str(root, "tag_name") ?? "",
            HtmlUrl = GitLab.GitLabJson.Str(root, "html_url") ?? "",
            Body = GitLab.GitLabJson.Str(root, "body"),
            Prerelease = GitLab.GitLabJson.Bool(root, "prerelease") ?? false,
            Assets = assets
        };
    }
}
