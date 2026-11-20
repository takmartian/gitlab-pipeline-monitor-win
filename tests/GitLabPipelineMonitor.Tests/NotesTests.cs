using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor.Tests;

/// <summary>release body 是 markdown，弹窗里是纯文本控件，符号必须清干净。</summary>
public static class NotesTests
{
    public static Task Run()
    {
        Check.Equal("这个版本没有写更新说明。", UpdateNotes.Plain(null), "空 body 给提示文案");
        Check.Equal("这个版本没有写更新说明。", UpdateNotes.Plain("   \n  \n"), "只有空白也算没有说明");

        var cleaned = UpdateNotes.Plain("## 标题\n\n正文内容\n\n- 项目一\n- 项目二");
        Check.NotContains("#", cleaned, "标题井号要去掉");
        Check.Contains("标题", cleaned, "标题文字要保留");
        Check.Contains("· 项目一", cleaned, "列表要变成中点");

        // 分隔线：实测会露出三个破折号，必须整行丢掉
        var noRule = UpdateNotes.Plain("上文\n\n---\n\n下文");
        Check.NotContains("---", noRule, "水平分隔线要整行去掉");
        Check.NotContains("***", UpdateNotes.Plain("a\n\n***\n\nb"), "星号分隔线也要去掉");
        Check.Contains("上文", noRule, "分隔线前内容保留");
        Check.Contains("下文", noRule, "分隔线后内容保留");

        var link = UpdateNotes.Plain("完整改动见 [仓库 README](https://example.com/a) 结尾");
        Check.NotContains("](", link, "链接语法要展开");
        Check.Contains("仓库 README (https://example.com/a)", link, "展开成文字加地址");

        var noFence = UpdateNotes.Plain("上文\n```\n这段代码不该出现\n```\n下文");
        Check.NotContains("这段代码不该出现", noFence, "代码块内容要丢掉");
        Check.Contains("下文", noFence, "代码块之后的内容保留");

        var marks = UpdateNotes.Plain("**粗体** 和 `行内代码` 和 ~~删除~~");
        Check.NotContains("*", marks, "加粗标记要去掉");
        Check.NotContains("`", marks, "反引号要去掉");
        Check.Contains("粗体 和 行内代码 和 删除", marks, "文字本身保留");

        Check.NotContains(">", UpdateNotes.Plain("> 引用内容"), "引用符号要去掉");
        Check.Contains("· 第一步", UpdateNotes.Plain("1. 第一步\n2. 第二步"), "有序列表也转中点");
        Check.NotContains("\n\n\n", UpdateNotes.Plain("一\n\n\n\n二"), "连续空行要压缩");
        Program.Ok("更新说明清理");
        return Task.CompletedTask;
    }
}
