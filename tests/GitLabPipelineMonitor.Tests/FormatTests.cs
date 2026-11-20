using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.Tests;

public static class FormatTests
{
    public static Task Run()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0);
        DateTime Ago(double seconds) => now.AddSeconds(-seconds);

        Loc.Language = AppLanguage.Zh;
        Check.Equal("—", Fmt.Relative(null), "没有时间给破折号");
        Check.Equal("0 秒前", Fmt.Relative(Ago(0), now), "刚刚");
        Check.Equal("30 秒前", Fmt.Relative(Ago(30), now), "30 秒");
        Check.Equal("1 分钟前", Fmt.Relative(Ago(60), now), "1 分钟");
        Check.Equal("10 分钟前", Fmt.Relative(Ago(600), now), "10 分钟");
        Check.Equal("1 小时前", Fmt.Relative(Ago(3600), now), "1 小时");
        Check.Equal("5 小时前", Fmt.Relative(Ago(5 * 3600), now), "5 小时");
        Check.Equal("昨天", Fmt.Relative(Ago(40 * 3600), now), "超过 36 小时算昨天");
        Check.Equal("4 天前", Fmt.Relative(Ago(4 * 86400), now), "4 天（超过昨天范围就按天算）");
        Check.Equal("2 周前", Fmt.Relative(Ago(20 * 86400), now), "2 周");
        Check.Equal("2 个月前", Fmt.Relative(Ago(70 * 86400), now), "2 个月");
        Check.Equal("2 年前", Fmt.Relative(Ago(800 * 86400), now), "2 年");

        Loc.Language = AppLanguage.En;
        Check.Equal("30 seconds ago", Fmt.Relative(Ago(30), now), "英文复数秒");
        Check.Equal("1 minute ago", Fmt.Relative(Ago(60), now), "英文单数分钟");
        Check.Equal("10 minutes ago", Fmt.Relative(Ago(600), now), "英文复数分钟");
        Check.Equal("yesterday", Fmt.Relative(Ago(40 * 3600), now), "英文昨天");
        Check.Equal("in 5 minutes", Fmt.Relative(now.AddMinutes(5), now), "未来时间用 in");
        Loc.Language = AppLanguage.Zh;

        Check.Equal("—", Fmt.Duration(null), "没有耗时给破折号");
        Check.Equal("45s", Fmt.Duration(45), "秒");
        Check.Equal("1m", Fmt.Duration(60), "整分不带 0s");
        Check.Equal("3m 12s", Fmt.Duration(192), "分秒");
        Check.Equal("1h 2m", Fmt.Duration(3720), "小时分钟");
        Check.Equal("2h", Fmt.Duration(7200), "整小时");
        Check.Equal("—", Fmt.Duration(-1), "负数当没有");

        Check.Equal("09:05:07", Fmt.Clock(new DateTime(2026, 1, 2, 9, 5, 7)), "时钟补零");
        Check.Equal("—", Fmt.Clock(null), "没有时钟给破折号");

        Check.Equal("gitlab.example.com", Fmt.Host("https://gitlab.example.com"), "主机名");
        Check.Equal("gitlab.example.com:8080", Fmt.Host("https://gitlab.example.com:8080"), "主机名带端口");
        Check.Equal("—", Fmt.Host(null), "空地址给破折号");
        Check.Equal("—", Fmt.Host("不是地址"), "坏地址给破折号");

        Program.Ok("格式化");
        return Task.CompletedTask;
    }
}

public static class LocTests
{
    public static Task Run()
    {
        Loc.Language = AppLanguage.Zh;
        Check.False(Loc.IsEnglish, "中文环境");
        Check.Equal("刷新", Loc.T("刷新"), "中文原样返回");

        Loc.Language = AppLanguage.En;
        Check.True(Loc.IsEnglish, "英文环境");
        Check.Equal("Refresh", Loc.T("刷新"), "英文查表");
        Check.Equal("Settings", Loc.T("设置"), "英文查表 2");
        Check.Equal("Update to 1.2.3", Loc.T("更新到 {0}", "1.2.3"), "带参数的译文");
        Check.Equal("5 projects · 3 pipelines", Loc.T("共 {0} 个项目 · {1} 条流水线", 5, 3), "多参数译文");

        // 查不到退回中文：这是这套轻量方案的安全网，新文案不会显示成 key
        Check.Equal("没见过的词条", Loc.T("没见过的词条"), "查不到退回中文");

        // 关键界面词必须有译文（漏一条英文界面就会夹生）
        foreach (var key in new[] { "全部", "运行中", "失败", "成功", "其他", "搜索", "详情", "重试", "取消",
                                    "流水线详情", "作业", "覆盖率", "耗时", "触发方式", "仅监控星标项目",
                                    "重新拉取项目列表", "测试连接", "自动检查更新", "立即更新", "忽略此版本",
                                    "登录时启动", "语言", "关于", "托盘图标显示状态" })
            Check.False(Loc.T(key) == key, "英文表缺词条：" + key);

        Loc.Language = AppLanguage.Zh;
        Program.Ok("本地化");
        return Task.CompletedTask;
    }
}
