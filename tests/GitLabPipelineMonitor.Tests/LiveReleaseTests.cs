using System.Text.Json;
using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor.Tests;

/// <summary>
/// 用真实的 GitHub Release 响应做解析验证 —— 数据是从 api.github.com 抓下来的
/// 本仓库 macOS 版的最新 release（Data/release.json），不是手写的理想样本。
/// 这类测试能挡住「字段名猜错」和「结构假设太乐观」两类问题。
/// </summary>
public static class LiveReleaseTests
{
    private static string ReadFile(string name)
    {
        // dotnet run 的工作目录是项目目录；两个候选路径兼容从不同位置启动
        var candidates = new[]
        {
            Path.Combine("Data", name),
            Path.Combine("tests", "GitLabPipelineMonitor.Tests", "Data", name)
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        throw new FileNotFoundException($"找不到测试数据 {name}");
    }

    public static Task Run()
    {
        using var document = JsonDocument.Parse(ReadFile("release.json"));
        var release = GitHubRelease.FromJson(document.RootElement.Clone());

        Check.Equal("v1.2.3", release.TagName, "tag_name 原样保留");
        Check.Equal("1.2.3", release.Version, "去掉 v 前缀得到版本号");
        Check.Equal(2, release.Assets.Count, "两个资产（dmg 与 zip）");
        Check.False(release.Prerelease, "不是预发布");
        Check.True(release.HtmlUrl.StartsWith("https://github.com/", StringComparison.Ordinal), "页面地址");
        Check.True(release.Body is { Length: > 0 }, "有更新说明");

        // 关键：优先挑 zip（解压就是文件，不用挂载 dmg），而不是列表里的第一个（那是 dmg）
        var preferred = release.PreferredAsset;
        Check.NotNull(preferred, "有可用资产");
        Check.True(preferred!.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), "优先选 zip 而不是排在前面的 dmg");

        // GitHub 在资产上给的 sha256 摘要要能拿到 —— 更新时靠它校验完整性
        Check.True(preferred.Digest is { Length: > 0 }, "资产带 digest");
        Check.True(preferred.Digest!.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase), "digest 是 sha256 前缀格式");
        Check.True(preferred.BrowserDownloadUrl.StartsWith("https://github.com/", StringComparison.Ordinal), "下载地址");
        Check.True(preferred.Size > 0, "资产大小");

        // 真实 release 的 body 是 markdown，清理后不该残留这些符号
        var notes = UpdateNotes.Plain(release.Body);
        Check.True(notes.Length > 0, "清理后有内容");
        Check.NotContains("](", notes, "链接语法要展开");
        Program.Ok("真实 GitHub Release 解析");

        // 限流时 GitHub 返回 403 + 一个没有 tag_name 的 JSON：解析不能崩
        using var limited = JsonDocument.Parse(ReadFile("ratelimited.json"));
        var empty = GitHubRelease.FromJson(limited.RootElement.Clone());
        Check.Equal("", empty.Version, "限流响应没有 tag_name，版本号应为空");
        Check.Equal(0, empty.Assets.Count, "限流响应没有资产");
        Check.IsNull(empty.PreferredAsset, "限流响应没有可用资产");
        Program.Ok("限流响应宽松解析");

        // 缺字段的 release 也不能崩
        using var sparse = JsonDocument.Parse("{\"tag_name\":\"v2.0\"}");
        var sparseRelease = GitHubRelease.FromJson(sparse.RootElement.Clone());
        Check.Equal("2.0", sparseRelease.Version, "缺前缀的 tag 也能解析");
        Check.Equal("这个版本没有写更新说明。", UpdateNotes.Plain(sparseRelease.Body), "缺 body 给提示文案");
        Program.Ok("缺字段宽松解析");
        return Task.CompletedTask;
    }
}
