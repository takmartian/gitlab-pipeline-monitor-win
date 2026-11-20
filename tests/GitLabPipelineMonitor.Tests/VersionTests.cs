using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor.Tests;

public static class VersionTests
{
    public static Task Run()
    {
        Check.Equal("1.2.3", AppVersion.Normalize("v1.2.3"), "去掉 v 前缀");
        Check.Equal("1.2.3", AppVersion.Normalize("  V1.2.3  "), "大小写与空白");
        Check.True(AppVersion.IsNewer("1.2.3", "1.2.2"), "1.2.3 比 1.2.2 新");
        // 这条最容易写错：按字符串比较会得出相反结论
        Check.True(AppVersion.IsNewer("1.1.10", "1.1.9"), "1.1.10 比 1.1.9 新（按数字分段）");
        Check.False(AppVersion.IsNewer("1.1.9", "1.1.10"), "1.1.9 不比 1.1.10 新");
        Check.True(AppVersion.IsNewer("2.0.0", "1.99.99"), "大版本跨越");
        Check.False(AppVersion.IsNewer("1.2.3", "1.2.3"), "同版本不算新");
        Check.False(AppVersion.IsNewer("1.2.2", "1.2.3"), "旧版本不算新");
        Check.False(AppVersion.IsNewer("1.2", "1.2.0"), "1.2 与 1.2.0 视为相等");
        Check.True(AppVersion.IsNewer("1.2.1", "1.2"), "1.2.1 比 1.2 新");
        Check.False(AppVersion.IsNewer(null, "1.0.0"), "null 不算新");
        Check.False(AppVersion.IsNewer("", "1.0.0"), "空串不算新");
        Check.False(AppVersion.IsNewer("dev", "1.0.0"), "dev 这种非数字不算新");
        Check.True(AppVersion.IsNewer("v1.3.0", "v1.2.9"), "带 v 前缀也能比");
        Check.Equal(3, AppVersion.Parse("1.2.3-beta").Count, "预发布后缀取数字段");
        Program.Ok("版本号比较");
        return Task.CompletedTask;
    }
}
