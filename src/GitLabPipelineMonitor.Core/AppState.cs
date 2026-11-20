using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Platform;
using GitLabPipelineMonitor.Core.Storage;

namespace GitLabPipelineMonitor.Core;

/// <summary>一个项目及其（已按筛选/搜索过滤的）流水线，面板按这个分组显示。</summary>
public sealed class ProjectGroup
{
    public required GitLabProject Project { get; init; }
    public required IReadOnlyList<Pipeline> Pipelines { get; init; }
    public bool HasActive => Pipelines.Any(p => p.Status.IsActive());
    public bool HasFailed => Pipelines.Any(p => p.Status == PipelineStatus.Failed);
    public Pipeline? Latest => Pipelines.Count == 0 ? null : Pipelines.OrderByDescending(p => p.SortDate).First();
}

/// <summary>
/// 全部运行时状态与刷新编排。这里是 macOS 版 AppState 的对应物，
/// 保留了那几个用真金白银换来的行为：刷新中的 force 合并、并发窗口限流、
/// 状态真的变了才通知。逻辑都在 Core 里，所以能在 mac 上跑测试。</summary>
public sealed class AppState : ObservableObject, IDisposable
{
    public AppSettings Settings { get; }
    private readonly IHostServices _host;
    private readonly IGitLabTransport _transport;

    // MARK: - 对外状态

    private GitLabUser? _user;
    public GitLabUser? User { get => _user; private set => Set(ref _user, value); }

    private string? _gitlabVersion;
    public string? GitlabVersion { get => _gitlabVersion; private set => Set(ref _gitlabVersion, value); }

    private IReadOnlyList<GitLabProject> _starredProjects = Array.Empty<GitLabProject>();
    public IReadOnlyList<GitLabProject> StarredProjects { get => _starredProjects; private set => Set(ref _starredProjects, value); }

    private IReadOnlyList<GitLabProject> _memberProjects = Array.Empty<GitLabProject>();
    public IReadOnlyList<GitLabProject> MemberProjects { get => _memberProjects; private set => Set(ref _memberProjects, value); }

    private IReadOnlyDictionary<int, IReadOnlyList<Pipeline>> _pipelines =
        new Dictionary<int, IReadOnlyList<Pipeline>>();
    public IReadOnlyDictionary<int, IReadOnlyList<Pipeline>> Pipelines { get => _pipelines; private set => Set(ref _pipelines, value); }

    /// <summary>单个项目的流水线加载错误（不至于整页崩）。</summary>
    private IReadOnlyDictionary<int, string> _projectErrors = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, string> ProjectErrors { get => _projectErrors; private set => Set(ref _projectErrors, value); }

    private DateTime? _lastUpdated;
    public DateTime? LastUpdated { get => _lastUpdated; private set => Set(ref _lastUpdated, value); }

    private bool _isLoadingProjects;
    public bool IsLoadingProjects { get => _isLoadingProjects; private set => Set(ref _isLoadingProjects, value); }

    private bool _isLoadingPipelines;
    public bool IsLoadingPipelines { get => _isLoadingPipelines; private set => Set(ref _isLoadingPipelines, value); }

    /// <summary>整体性错误：401 / 地址错 / 网络不通。</summary>
    private string? _fatalErrorMessage;
    public string? FatalErrorMessage { get => _fatalErrorMessage; private set => Set(ref _fatalErrorMessage, value); }

    /// <summary>最近一次操作（重试、取消等）的反馈，UI 显示成 toast。</summary>
    private string? _actionMessage;
    public string? ActionMessage { get => _actionMessage; set => Set(ref _actionMessage, value); }

    /// <summary>正在执行的操作标识，用来只让对应那一行的按钮转圈。</summary>
    private string? _busyAction;
    public string? BusyAction { get => _busyAction; private set => Set(ref _busyAction, value); }

    /// <summary>演示模式下顶栏显示的假主机名。</summary>
    public string? DemoHost { get; set; }

    private AggregateStatus _aggregate = AggregateStatus.None;
    public AggregateStatus Aggregate { get => _aggregate; private set => Set(ref _aggregate, value); }

    private int _failingCount;
    public int FailingCount { get => _failingCount; private set => Set(ref _failingCount, value); }

    /// <summary>托盘图标要据此重画，由宿主挂上。</summary>
    public event Action? IconNeedsUpdate;

    private bool _running;

    /// <summary>刷新进行中又收到 force：等这轮结束立刻补一轮。</summary>
    private bool _pendingForce;

    private readonly Dictionary<int, PipelineStatus> _previousStatus = new();
    private System.Threading.Timer? _timer;

    /// <summary>设置项变化的防抖：地址、令牌、勾选项目这些改动要稍等一下再触发刷新，
    /// 否则逐字输入会连着打好几轮。</summary>
    private readonly Debouncer _addressDebouncer = new(TimeSpan.FromMilliseconds(1000));
    private readonly Debouncer _tokenDebouncer = new(TimeSpan.FromMilliseconds(1200));
    private readonly Debouncer _selectionDebouncer = new(TimeSpan.FromMilliseconds(400));
    private readonly Debouncer _limitDebouncer = new(TimeSpan.FromMilliseconds(500));

    public AppState(AppSettings settings, IHostServices host, IGitLabTransport transport)
    {
        Settings = settings;
        _host = host;
        _transport = transport;
        ObserveSettings();
        RestartTimer();
    }

    // MARK: - 派生数据

    /// <summary>星标 + 参与项目合并去重，星标优先。</summary>
    public IReadOnlyList<GitLabProject> AvailableProjects
    {
        get
        {
            var merged = new Dictionary<int, GitLabProject>();
            foreach (var project in MemberProjects) merged[project.Id] = project;
            foreach (var project in StarredProjects) merged[project.Id] = project;
            return merged.Values.OrderByDescending(p => p.LastActivityAt ?? DateTime.MinValue).ToList();
        }
    }

    public IReadOnlyList<GitLabProject> MonitoredProjects
    {
        get
        {
            if (Settings.OnlyStarred) return StarredProjects;
            var selected = new HashSet<int>(Settings.MonitoredProjectIds);
            return AvailableProjects.Where(p => selected.Contains(p.Id)).ToList();
        }
    }

    /// <summary>每个监控项目的最新一条流水线：图标和汇总看这个。</summary>
    public IReadOnlyList<Pipeline> LatestPerProject
        => MonitoredProjects
            .Select(p => Pipelines.TryGetValue(p.Id, out var list) ? list.OrderByDescending(x => x.SortDate).FirstOrDefault() : null)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

    public IReadOnlyList<Pipeline> AllVisiblePipelines
        => MonitoredProjects
            .SelectMany(p => Pipelines.TryGetValue(p.Id, out var list) ? list : Array.Empty<Pipeline>())
            .ToList();

    public GitLabProject? ProjectFor(Pipeline pipeline)
    {
        if (pipeline.ProjectId is not { } id) return null;
        return StarredProjects.FirstOrDefault(p => p.Id == id)
            ?? MemberProjects.FirstOrDefault(p => p.Id == id)
            ?? MonitoredProjects.FirstOrDefault(p => p.Id == id);
    }

    public GitLabClient? Client()
    {
        var url = Settings.NormalizedBaseUrl;
        var token = Settings.Token.Trim();
        if (url is null || token.Length == 0) return null;
        return new GitLabClient(url, token, Settings.AuthMode, _transport);
    }
    // MARK: - 生命周期

    public void Bootstrap()
    {
        if (Settings.IsConfigured) Refresh(force: true);
    }

    /// <summary>地址或令牌变了：清掉流水线数据重来。
    /// keepProjects = true 用于「同一实例换令牌」：保留项目列表，
    /// 否则刷新一慢或 momentarily 没有可用令牌，项目列表会凭空消失。</summary>
    public void ResetAndRefresh(bool keepProjects = false)
    {
        if (!keepProjects)
        {
            User = null;
            GitlabVersion = null;
            StarredProjects = Array.Empty<GitLabProject>();
            MemberProjects = Array.Empty<GitLabProject>();
        }
        Pipelines = new Dictionary<int, IReadOnlyList<Pipeline>>();
        ProjectErrors = new Dictionary<int, string>();
        _previousStatus.Clear();
        LastUpdated = null;
        FatalErrorMessage = null;
        // 不取消正在跑的那轮（会和 running 打架）：交给 pendingForce 合并补一轮
        Refresh(force: true);
    }

    public void Refresh(bool force = false)
    {
        if (!Settings.IsConfigured || Settings.DemoMode) return;
        if (_running)
        {
            // 正在刷新时不再「取消 + 重启」：被取消那轮的 running 还没复位，
            // 新一轮会撞 guard !running 直接返回，整轮刷新静默空转，数据就停在半路
            // （表现为项目/流水线时不时空掉，要等下一次定时器才回来）。
            // 这里改成合并：记一笔「这轮结束后再来一轮」。
            if (force) _pendingForce = true;
            return;
        }
        _ = RunRefreshAsync();
    }

    private async Task RunRefreshAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            await RunRefreshBodyAsync().ConfigureAwait(false);
        }
        finally
        {
            _running = false;
            if (_pendingForce)
            {
                _pendingForce = false;
                Refresh(force: true);
            }
        }
    }

    private async Task RunRefreshBodyAsync()
    {
        var client = Client();
        if (client is null)
        {
            FatalErrorMessage = new GitLabException(GitLabErrorKind.NotConfigured).Message;
            RecomputeAggregate();
            return;
        }

        // 顶栏要显示账号；单独一条任务并行拉，不串在流水线前面拖慢首屏
        if (User is null)
        {
            _ = Task.Run(async () =>
            {
                var c = Client();
                if (c is null) return;
                try { User = await c.CurrentUserAsync().ConfigureAwait(false); } catch (GitLabException) { }
                if (GitlabVersion is null)
                {
                    try { GitlabVersion = (await c.VersionAsync().ConfigureAwait(false)).Version; }
                    catch (GitLabException) { }
                }
            });
        }

        var needsProjects = Settings.OnlyStarred ? StarredProjects.Count == 0 : AvailableProjects.Count == 0;
        if (needsProjects) await LoadProjectsAsync(client).ConfigureAwait(false);
        else await LoadStarredAsync(client).ConfigureAwait(false);

        await RefreshPipelinesAsync(client).ConfigureAwait(false);
        LastUpdated = DateTime.Now;
        RecomputeAggregate();
        IconNeedsUpdate?.Invoke();
    }

    private void RecomputeAggregate()
    {
        if (FatalErrorMessage is not null && !Settings.IsConfigured) { Aggregate = AggregateStatus.Error; return; }
        var list = LatestPerProject;
        FailingCount = list.Count(p => p.Status == PipelineStatus.Failed);
        if (list.Count == 0)
        {
            Aggregate = FatalErrorMessage is null ? AggregateStatus.None : AggregateStatus.Error;
            return;
        }
        if (list.Any(p => p.Status == PipelineStatus.Failed)) Aggregate = AggregateStatus.Failed;
        else if (list.Any(p => p.Status == PipelineStatus.Running)) Aggregate = AggregateStatus.Running;
        else if (list.Any(p => p.Status.IsActive())) Aggregate = AggregateStatus.Pending;
        else if (list.Any(p => p.Status == PipelineStatus.Success)) Aggregate = AggregateStatus.Ok;
        else Aggregate = AggregateStatus.None;
    }

    // MARK: - 项目列表

    public void LoadProjects(bool force = false)
    {
        var client = Client();
        if (client is null) return;
        _ = Task.Run(async () =>
        {
            if (force) { StarredProjects = Array.Empty<GitLabProject>(); MemberProjects = Array.Empty<GitLabProject>(); }
            // 用户主动点「重新拉取项目列表」：星标和参与项目都要拉。
            // 否则开着「仅监控星标」时列表只剩星标，和连接页报的参与项目数对不上
            var starred = LoadStarredAsync(client);
            var member = LoadMemberAsync(client);
            await Task.WhenAll(starred, member).ConfigureAwait(false);
            await RefreshPipelinesAsync(client).ConfigureAwait(false);
            LastUpdated = DateTime.Now;
            RecomputeAggregate();
            IconNeedsUpdate?.Invoke();
        });
    }

    private async Task LoadProjectsAsync(GitLabClient client)
    {
        // 只监控星标时不拉「参与项目」：那可能是几百个项目、要翻好几页，
        // 每次刷新都等它的话，面板要空好几秒才能看到流水线
        if (Settings.OnlyStarred) { await LoadStarredAsync(client).ConfigureAwait(false); return; }
        await Task.WhenAll(LoadStarredAsync(client), LoadMemberAsync(client)).ConfigureAwait(false);
    }

    private async Task LoadStarredAsync(GitLabClient client)
    {
        if (IsLoadingProjects) return;
        IsLoadingProjects = true;
        try
        {
            try
            {
                var list = await client.StarredProjectsAsync().ConfigureAwait(false);
                StarredProjects = list;
                FatalErrorMessage = null;
                if (!Settings.OnlyStarred && Settings.MonitoredProjectIds.Count == 0)
                    // 第一次切到手动选择时，默认把星标项目带上，别给用户一个空列表
                    Settings.SetMonitoredProjectIds(list.Select(p => p.Id));
            }
            catch (GitLabException error)
            {
                FatalErrorMessage = error.Message;
            }
        }
        finally
        {
            IsLoadingProjects = false;
        }
    }

    private async Task LoadMemberAsync(GitLabClient client)
    {
        try { MemberProjects = await client.MemberProjectsAsync().ConfigureAwait(false); }
        catch (GitLabException) { /* 参与项目拉不到不影响主流程 */ }
    }

    /// <summary>日常自动刷新在「仅监控星标」时不拉参与项目，打开设置的项目页时补这一次。</summary>
    public void EnsureMemberProjects()
    {
        if (MemberProjects.Count > 0) return;
        var client = Client();
        if (client is null) return;
        _ = Task.Run(() => LoadMemberAsync(client));
    }
    // MARK: - 流水线

    public async Task RefreshPipelinesAsync(GitLabClient? client = null)
    {
        client ??= Client();
        if (client is null) return;
        await RefreshPipelinesCoreAsync(client).ConfigureAwait(false);
        LastUpdated = DateTime.Now;
        RecomputeAggregate();
        IconNeedsUpdate?.Invoke();
    }

    private async Task RefreshPipelinesCoreAsync(GitLabClient client)
    {
        var targets = MonitoredProjects;
        if (targets.Count == 0)
        {
            Pipelines = new Dictionary<int, IReadOnlyList<Pipeline>>();
            ProjectErrors = new Dictionary<int, string>();
            return;
        }
        IsLoadingPipelines = true;
        try
        {
            var limit = Math.Max(1, Math.Min(Settings.PipelinesPerProject, 20));
            var results = new Dictionary<int, IReadOnlyList<Pipeline>>();
            var errors = new Dictionary<int, string>();
            string? unauthorized = null;

            // 并发窗口 6：监控项目多时也不会把 GitLab 打爆
            await RunWindowedAsync(targets, 6, async project =>
            {
                try
                {
                    var list = await client.PipelinesAsync(project.Id, limit).ConfigureAwait(false);
                    lock (results) results[project.Id] = list;
                }
                catch (GitLabException error)
                {
                    lock (errors) errors[project.Id] = error.Message;
                    if (error.Kind == GitLabErrorKind.Unauthorized) unauthorized = error.Message;
                }
            }).ConfigureAwait(false);

            // 列表接口不返回触发人 / 耗时 / 覆盖率，逐条补一次详情
            await EnrichPipelinesAsync(client, targets, results).ConfigureAwait(false);

            if (unauthorized is not null) FatalErrorMessage = unauthorized;
            else if (results.Count == 0 && errors.Count > 0 && targets.Count == 1) FatalErrorMessage = errors.Values.First();
            else if (results.Count > 0) FatalErrorMessage = null;

            NotifyTransitions(results);
            Pipelines = results;
            ProjectErrors = errors;
        }
        finally
        {
            IsLoadingPipelines = false;
        }
    }

    /// <summary>按固定并发度跑一批任务。等价于 macOS 版的 withTaskGroup 分块。</summary>
    internal static async Task RunWindowedAsync<T>(IReadOnlyList<T> items, int window, Func<T, Task> body)
    {
        for (var start = 0; start < items.Count; start += window)
        {
            var batch = items.Skip(start).Take(window).Select(body);
            await Task.WhenAll(batch).ConfigureAwait(false);
        }
    }

    /// <summary>GitLab 的流水线列表接口不返回 user / duration / coverage，只有单条详情接口才有。
    /// 这里按面板实际要显示的范围逐条补一次详情；某条失败就保留列表里的原值，不影响其它条。</summary>
    private static async Task EnrichPipelinesAsync(GitLabClient client, IReadOnlyList<GitLabProject> targets,
        Dictionary<int, IReadOnlyList<Pipeline>> results)
    {
        var pending = new List<(int ProjectId, int PipelineId)>();
        foreach (var project in targets)
            foreach (var item in results.TryGetValue(project.Id, out var list) ? list : Array.Empty<Pipeline>())
                pending.Add((project.Id, item.Id));
        if (pending.Count == 0) return;

        // 并发窗口 10：监控项目多时也不会把 GitLab 打爆
        await RunWindowedAsync(pending, 10, async entry =>
        {
            Pipeline? detail;
            try { detail = await client.PipelineDetailAsync(entry.ProjectId, entry.PipelineId).ConfigureAwait(false); }
            catch (GitLabException) { return; }
            if (detail is null) return;
            lock (results)
            {
                if (!results.TryGetValue(entry.ProjectId, out var listValue)) return;
                var mutable = listValue.ToList();
                var index = mutable.FindIndex(p => p.Id == entry.PipelineId);
                if (index < 0) return;
                mutable[index].MergeDetail(detail);
                results[entry.ProjectId] = mutable;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>只在「状态真的变了」时通知，避免每次刷新都弹一遍。</summary>
    private void NotifyTransitions(IReadOnlyDictionary<int, IReadOnlyList<Pipeline>> results)
    {
        var next = new Dictionary<int, PipelineStatus>();
        var failures = new List<(GitLabProject Project, Pipeline Pipeline)>();
        var recoveries = new List<(GitLabProject Project, Pipeline Pipeline)>();
        var allProjects = StarredProjects.Concat(MemberProjects).ToList();

        foreach (var (projectId, list) in results)
        {
            var project = allProjects.FirstOrDefault(p => p.Id == projectId);
            foreach (var pipeline in list)
            {
                next[pipeline.Id] = pipeline.Status;
                if (!_previousStatus.TryGetValue(pipeline.Id, out var old) || old == pipeline.Status) continue;
                if (project is null) continue;
                if (pipeline.Status == PipelineStatus.Failed)
                    failures.Add((project, pipeline));
                else if (pipeline.Status == PipelineStatus.Success &&
                         old is PipelineStatus.Failed or PipelineStatus.Running)
                    recoveries.Add((project, pipeline));
            }
        }
        _previousStatus.Clear();
        foreach (var pair in next) _previousStatus[pair.Key] = pair.Value;

        if (!Settings.NotifyOnFailure && !Settings.NotifyOnRecovery) return;
        if (Settings.NotifyOnFailure)
            foreach (var item in failures.Take(4))
                _host.Notify(Loc.T("流水线失败"), $"{item.Project.PathWithNamespace} · {item.Pipeline.Ref}",
                    $"pipeline-{item.Pipeline.Id}-failed");
        if (Settings.NotifyOnRecovery)
            foreach (var item in recoveries.Take(4))
                _host.Notify(Loc.T("流水线已恢复"), $"{item.Project.PathWithNamespace} · {item.Pipeline.Ref}",
                    $"pipeline-{item.Pipeline.Id}-success");
    }
    // MARK: - 操作

    public void Retry(Pipeline pipeline)
    {
        var client = Client();
        if (pipeline.ProjectId is not { } projectId || client is null) return;
        BusyAction = $"pipeline-{pipeline.Id}-retry";
        _ = RunActionAsync(async () =>
        {
            await client.RetryPipelineAsync(projectId, pipeline.Id).ConfigureAwait(false);
            await RefreshPipelinesAsync(client).ConfigureAwait(false);
        });
    }

    public void Cancel(Pipeline pipeline)
    {
        var client = Client();
        if (pipeline.ProjectId is not { } projectId || client is null) return;
        BusyAction = $"pipeline-{pipeline.Id}-cancel";
        _ = RunActionAsync(async () =>
        {
            await client.CancelPipelineAsync(projectId, pipeline.Id).ConfigureAwait(false);
            await RefreshPipelinesAsync(client).ConfigureAwait(false);
        });
    }

    /// <summary>打星 / 取消打星：成功后本地立刻改标记，监控范围随之变化。</summary>
    public void SetStarred(GitLabProject project, bool starred)
    {
        var client = Client();
        if (client is null) return;
        BusyAction = $"star-{project.Id}";
        _ = RunActionAsync(async () =>
        {
            if (starred) await client.StarAsync(project.Id).ConfigureAwait(false);
            else await client.UnstarAsync(project.Id).ConfigureAwait(false);
            ApplyStar(project.Id, starred);
            await RefreshPipelinesAsync(client).ConfigureAwait(false);
        });
    }

    /// <summary>本地立刻反映打星结果，不等下一轮拉取。</summary>
    public void ApplyStar(int id, bool starred)
    {
        var starredList = StarredProjects.ToList();
        var memberList = MemberProjects.ToList();

        var index = starredList.FindIndex(p => p.Id == id);
        if (index >= 0)
        {
            starredList[index].Starred = starred;
            if (!starred) starredList.RemoveAt(index);
        }
        else if (starred)
        {
            var from = memberList.FirstOrDefault(p => p.Id == id);
            if (from is not null)
            {
                // 放进星标列表的是副本，不是参与列表里那个对象：
                // 否则下面调整参与列表星数时会把同一份加两次
                var copy = from.Clone();
                copy.Starred = true;
                copy.StarCount = from.StarCount + 1;
                starredList.Insert(0, copy);
            }
        }

        var memberIndex = memberList.FindIndex(p => p.Id == id);
        if (memberIndex >= 0)
        {
            memberList[memberIndex].Starred = starred;
            memberList[memberIndex].StarCount = Math.Max(0,
                memberList[memberIndex].StarCount + (starred ? 1 : -1));
        }

        StarredProjects = starredList;
        MemberProjects = memberList;
        if (!starred) Settings.SetMonitoredProjectIds(Settings.MonitoredProjectIds.Where(x => x != id));
        OnPropertyChanged(nameof(StarredProjects));
        Refresh(nameof(AvailableProjects), nameof(MonitoredProjects), nameof(LatestPerProject), nameof(AllVisiblePipelines));
    }

    public void RunPipeline(GitLabProject project, string reference)
    {
        var client = Client();
        if (client is null) return;
        BusyAction = $"project-{project.Id}-run";
        _ = RunActionAsync(async () =>
        {
            await client.CreatePipelineAsync(project.Id, reference).ConfigureAwait(false);
            ActionMessage = Loc.T("流水线已创建");
            await RefreshPipelinesAsync(client).ConfigureAwait(false);
        });
    }

    public void RetryJob(Job job, int projectId)
    {
        var client = Client();
        if (client is null) return;
        BusyAction = $"job-{job.Id}-retry";
        _ = RunActionAsync(async () =>
        {
            await client.RetryJobAsync(projectId, job.Id).ConfigureAwait(false);
            await RefreshPipelinesAsync(client).ConfigureAwait(false);
        });
    }

    public void CancelJob(Job job, int projectId)
    {
        var client = Client();
        if (client is null) return;
        BusyAction = $"job-{job.Id}-cancel";
        _ = RunActionAsync(async () =>
        {
            await client.CancelJobAsync(projectId, job.Id).ConfigureAwait(false);
            await RefreshPipelinesAsync(client).ConfigureAwait(false);
        });
    }

    /// <summary>操作的统一收尾：出错就把人话放进 ActionMessage，无论成败都清掉转圈标记。</summary>
    private async Task RunActionAsync(Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (GitLabException error)
        {
            ActionMessage = error.Message;
        }
        catch (Exception error)
        {
            ActionMessage = error.Message;
        }
        finally
        {
            BusyAction = null;
        }
    }

    // MARK: - 详情数据

    public async Task<(IReadOnlyList<Job>? Jobs, string? Error)> FetchJobsAsync(int projectId, int pipelineId)
    {
        var client = Client();
        if (client is null) return (null, new GitLabException(GitLabErrorKind.NotConfigured).Message);
        try { return (await client.JobsAsync(projectId, pipelineId).ConfigureAwait(false), null); }
        catch (GitLabException error) { return (null, error.Message); }
    }

    public async Task<(IReadOnlyList<Branch>? Branches, string? Error)> FetchBranchesAsync(int projectId)
    {
        var client = Client();
        if (client is null) return (null, new GitLabException(GitLabErrorKind.NotConfigured).Message);
        try { return (await client.BranchesAsync(projectId).ConfigureAwait(false), null); }
        catch (GitLabException error) { return (null, error.Message); }
    }

    public async Task<Commit?> FetchCommitAsync(int projectId, string sha)
    {
        var client = Client();
        if (client is null) return null;
        try { return await client.CommitAsync(projectId, sha).ConfigureAwait(false); }
        catch (GitLabException) { return null; }
    }

    // MARK: - 连接测试

    public sealed record TestResult(GitLabUser? User, string? Version, int StarredCount, int MemberCount);

    public async Task<(TestResult? Result, string? Error)> TestConnectionAsync()
    {
        var client = Client();
        if (client is null) return (null, new GitLabException(GitLabErrorKind.NotConfigured).Message);
        try
        {
            var user = await client.CurrentUserAsync().ConfigureAwait(false);
            string? version = null;
            try { version = (await client.VersionAsync().ConfigureAwait(false)).Version; } catch (GitLabException) { }
            int starred = 0, member = 0;
            try { starred = (await client.StarredProjectsAsync().ConfigureAwait(false)).Count; } catch (GitLabException) { }
            try { member = (await client.MemberProjectsAsync().ConfigureAwait(false)).Count; } catch (GitLabException) { }
            return (new TestResult(user, version, starred, member), null);
        }
        catch (GitLabException error)
        {
            return (null, error.Message);
        }
    }

    /// <summary>演示模式一次性灌入数据：属性 setter 是私有的（只有刷新编排能改状态），
    /// 演示数据走这个入口，避免为了演示把状态全部开放出去。</summary>
    public void LoadDemoData(GitLabUser user, string version, IReadOnlyList<GitLabProject> starred,
        IReadOnlyList<GitLabProject> member, Dictionary<int, IReadOnlyList<Pipeline>> pipelines)
    {
        User = user;
        GitlabVersion = version;
        FatalErrorMessage = null;
        StarredProjects = starred;
        MemberProjects = member;
        Pipelines = pipelines;
        ProjectErrors = new Dictionary<int, string>();
        LastUpdated = DateTime.Now;
        RecomputeAggregate();
        IconNeedsUpdate?.Invoke();
    }

    // MARK: - 定时器 / 观察

    public void RestartTimer()
    {
        _timer?.Dispose();
        _timer = null;
        var interval = Settings.RefreshInterval;
        if (interval <= 0 || Settings.AutoRefreshPaused) return;
        _timer = new System.Threading.Timer(_ => Refresh(), null,
            TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
    }

    private void ObserveSettings()
    {
        // 地址、令牌这类逐字输入的项要防抖；勾选、数值这类改动稍等一下再刷。
        // 这条经验来自 macOS 版：不去抖的话，打开设置窗口就会误触发「地址变了」，
        // 把项目列表整个清空重拉（面板会闪一下空）。
        Settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.RefreshInterval):
                    RestartTimer();
                    break;
                case nameof(AppSettings.ServerUrl):
                    _addressDebouncer.Debounce(() => ResetAndRefresh());
                    break;
                case nameof(AppSettings.Token):
                    // 换令牌：同一台服务器，项目列表没必要清空
                    // （清空后如果这次刷新没成功，列表就一直空着）
                    _tokenDebouncer.Debounce(() => ResetAndRefresh(keepProjects: true));
                    break;
                case nameof(AppSettings.OnlyStarred):
                    ResetAndRefresh(keepProjects: true);
                    EnsureMemberProjects();
                    break;
                case nameof(AppSettings.MonitoredProjectIds):
                    _selectionDebouncer.Debounce(() => Refresh(force: true));
                    break;
                case nameof(AppSettings.PipelinesPerProject):
                    _limitDebouncer.Debounce(() => Refresh(force: true));
                    break;
                case nameof(AppSettings.AutoRefreshPaused):
                    RestartTimer();
                    break;
                case null:
                    // 语言切换：所有已显示的文案都要重算
                    OnPropertyChanged(null);
                    break;
            }
        };
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _addressDebouncer.Dispose();
        _tokenDebouncer.Dispose();
        _selectionDebouncer.Dispose();
        _limitDebouncer.Dispose();
    }
}
