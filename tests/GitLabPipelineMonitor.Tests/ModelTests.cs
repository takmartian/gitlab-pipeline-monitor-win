using System.Text.Json;
using GitLabPipelineMonitor.Core.GitLab;

namespace GitLabPipelineMonitor.Tests;

/// <summary>模型解析：自建 GitLab 版本差异很大，字段缺失或类型不符不能让整页打不开。</summary>
public static class ModelTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public static Task Run()
    {
        // 正常样本：字段齐全
        var project = GitLabProject.FromJson(Json("""
            {"id":42,"name":"customer-admin","path_with_namespace":"web/customer-admin",
             "web_url":"https://gitlab.example.com/web/customer-admin","default_branch":"develop",
             "last_activity_at":"2026-11-20T10:30:00.123Z","star_count":7,"archived":false}
            """), true);
        Check.Equal(42, project.Id, "项目 id");
        Check.Equal("web/customer-admin", project.PathWithNamespace, "完整路径");
        Check.Equal("customer-admin", project.ShortPath, "短名取最后一段");
        Check.Equal("web", project.Namespace, "命名空间取除末段");
        Check.Equal("https://gitlab.example.com/web/customer-admin/-/pipelines",
            project.PipelinesUrl("https://gitlab.example.com"), "流水线页地址用 /-/ 新路径");
        Check.True(project.Starred, "星标标记由调用方给");
        Check.NotNull(project.LastActivityAt, "带小数秒的日期要能解析");

        // 字段全缺：不能抛，全部走默认值
        var empty = GitLabProject.FromJson(Json("{}"), false);
        Check.Equal(0, empty.Id, "缺 id 给 0");
        Check.Equal("project", empty.Name, "缺 name 给占位名");
        Check.Equal("", empty.WebUrl, "缺 web_url 给空串");
        Check.False(empty.Archived, "缺 archived 当 false");

        // web_url 缺失时要用服务器地址拼出来，否则「在项目页打开」点开空串
        var noWebUrl = GitLabProject.FromJson(Json("{\"id\":1,\"path_with_namespace\":\"web/customer-admin\"}"), false);
        Check.Equal("https://gitlab.example.com/web/customer-admin",
            noWebUrl.ResolvedWebUrl("https://gitlab.example.com"), "web_url 缺失时用服务器地址拼出项目页");
        Check.Equal("https://gitlab.example.com/project/-/pipelines/7",
            empty.PipelineUrl(new Pipeline { Id = 7 }, "https://gitlab.example.com"), "流水线详情地址用 /-/ 路径");
        Check.Equal("", empty.ResolvedWebUrl(null), "没有服务器地址时给空串而不是乱拼");

        // 日期：ISO 带小数秒、不带小数秒、Unix 秒数、null、类型错
        Check.NotNull(GitLabDate.Parse("2026-11-20T10:30:00.123Z"), "带小数秒");
        Check.NotNull(GitLabDate.Parse("2026-11-20T10:30:00Z"), "不带小数秒");
        Check.IsNull(GitLabDate.Parse("不是日期"), "坏日期给 null");
        Check.IsNull(GitLabDate.Parse(""), "空串给 null");
        Check.NotNull(GitLabJson.Date(Json("{\"d\":1700000000}"), "d"), "Unix 秒数要能当日期");
        Check.IsNull(GitLabJson.Date(Json("{\"d\":\"不是日期\"}"), "d"), "日期字段是坏字符串给 null");
        Check.IsNull(GitLabJson.Date(Json("{}"), "d"), "字段缺失给 null");

        // 类型不符要宽松处理，不能让整次解析失败
        Check.Equal(7, GitLabJson.Int(Json("{\"n\":\"7\"}"), "n"), "数字写成字符串也要取到");
        Check.IsNull(GitLabJson.Int(Json("{\"n\":\"abc\"}"), "n"), "非数字字符串给 null");
        Check.True(GitLabJson.Bool(Json("{\"b\":true}"), "b") ?? false, "布尔取值");
        Check.IsNull(GitLabJson.Bool(Json("{\"b\":\"yes\"}"), "b"), "非布尔给 null");

        // 流水线：coverage 字段名、状态字符串映射、short sha
        var pipeline = Pipeline.FromJson(Json("""
            {"id":9001,"iid":482,"sha":"a1b2c3d4e5f6aabb","ref":"feature/kyc",
             "status":"running","created_at":"2026-11-20T10:00:00Z",
             "updated_at":"2026-11-20T10:05:00Z","coverage":"87.2",
             "user":{"id":2,"username":"demo.user","name":"演示用户"}}
            """));
        Check.Equal(9001, pipeline.Id, "流水线 id");
        Check.Equal("a1b2c3d4", pipeline.ShortSha, "SHA 取前 8 位");
        Check.Equal(PipelineStatus.Running, pipeline.Status, "状态映射");
        Check.Equal("87.2", pipeline.Coverage ?? "", "coverage 字段名");
        Check.Equal("demo.user", pipeline.User?.Username ?? "", "触发人");
        Check.Equal("2026-11-20 10:05", pipeline.SortDate.ToString("yyyy-MM-dd HH:mm"), "排序用 updated_at");

        // 列表接口不返回 project_id，加载时补齐
        Check.IsNull(pipeline.ProjectId, "列表结果里没有 project_id");

        // 认不出的状态一律 Unknown，而不是抛
        Check.Equal(PipelineStatus.Unknown, PipelineStatusExtensions.Parse("teleporting"), "未知状态");
        Check.Equal(PipelineStatus.Unknown, PipelineStatusExtensions.Parse(null), "null 状态算未知");
        Check.Equal(PipelineStatus.Unknown, PipelineStatusExtensions.Parse(""), "空串状态算未知");

        // mergeDetail 只补空字段，不许把列表里较新的状态说旧
        var target = Pipeline.FromJson(Json("{\"id\":1,\"iid\":1,\"sha\":\"aa\",\"ref\":\"main\",\"status\":\"success\"}"));
        var detail = Pipeline.FromJson(Json("{\"id\":1,\"iid\":1,\"sha\":\"aa\",\"ref\":\"main\",\"status\":\"running\",\"duration\":120,\"coverage\":\"91\",\"user\":{\"id\":9,\"username\":\"bot\",\"name\":\"bot\"}}"));
        target.MergeDetail(detail);
        Check.Equal(120d, target.Duration ?? 0, "详情补耗时");
        Check.Equal("91", target.Coverage ?? "", "详情补覆盖率");
        Check.Equal("bot", target.User?.Username ?? "", "详情补触发人");
        Check.Equal(PipelineStatus.Success, target.Status, "状态以列表为准，不被详情改旧");

        Program.Ok("模型解析");
        return Task.CompletedTask;
    }
}
