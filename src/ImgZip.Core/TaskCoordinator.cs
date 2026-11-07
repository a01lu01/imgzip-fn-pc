using System.Collections.ObjectModel;

namespace ImgZip.Core;

/// <summary>
/// 应用级任务协调者：持有全部任务、名额分配、持久化写入与权威配置。
/// 阶段 1b 中仍是实例级——每个 <see cref="MainViewModel"/> 自建一个，行为与拆分前的单窗口一致；
/// 传入同一个实例即可让多个窗口共享名额池。
/// </summary>
public sealed class TaskCoordinator
{
    private readonly IWorkerClient worker;
    private readonly ITaskStore taskStore;
    private readonly Dictionary<string, CompressionTaskViewModel> taskIndex = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim taskGate = new(1, 1);
    private readonly List<Attachment> attachments = [];
    private readonly SemaphoreSlim configGate = new(1, 1);
    private Task? recovery;

    public TaskCoordinator(IWorkerClient worker, ITaskStore taskStore, Action<Action>? dispatch = null)
    {
        this.worker = worker; this.taskStore = taskStore;
        Dispatch = dispatch ?? (action => action());
    }

    /// <summary>默认派发器；单窗口时所有生命周期转换都在它上面串行执行。</summary>
    public Action<Action> Dispatch { get; }

    // ---- 窗口挂接 ----
    /// <summary>
    /// 一个窗口在协调者中的身份。持有主任务归属与派发器，Detach 后不再收到任何回调。
    /// </summary>
    public sealed class Attachment : IDisposable
    {
        internal Attachment(TaskCoordinator owner) => Owner = owner;
        internal TaskCoordinator Owner { get; }
        internal HashSet<string> Jobs { get; } = new(StringComparer.Ordinal);
        internal Action<CompressionTaskViewModel>? SinkCallback;
        internal Action<CompressionTaskViewModel>? MirrorCallback;
        internal Action? TasksChangedCallback;
        internal Action? DerivedChangedCallback;
        internal Action<string>? ReportCallback;
        internal Action? ConfigChangedCallback;
        public bool IsAttached { get; private set; } = true;
        /// <summary>该窗口是否持有此任务（用于按 JobId 路由，避免广播给所有窗口）。</summary>
        public bool Owns(string jobId) => Jobs.Contains(jobId);
        public void Dispose()
        {
            if (!IsAttached) return;
            IsAttached = false;
            Owner.Detach(this);
        }
    }

    public Attachment Attach(
        Action<CompressionTaskViewModel> sink,
        Action<CompressionTaskViewModel> mirror,
        Action tasksChanged,
        Action derivedChanged,
        Action<string> report,
        Action configChanged)
    {
        var attachment = new Attachment(this)
        {
            SinkCallback = sink, MirrorCallback = mirror, TasksChangedCallback = tasksChanged,
            DerivedChangedCallback = derivedChanged, ReportCallback = report,
            ConfigChangedCallback = configChanged
        };
        attachments.Add(attachment);
        return attachment;
    }
    private void Detach(Attachment attachment) => attachments.Remove(attachment);
    private IEnumerable<Attachment> Live => attachments.Where(a => a.IsAttached);

    /// <summary>任务所属窗口；未登记时归第一个挂接的窗口（单窗口与拆分前等价）。</summary>
    private Attachment? OwnerOf(string jobId) => Live.FirstOrDefault(a => a.Jobs.Contains(jobId));
    private Attachment? Default => Live.FirstOrDefault();

    private void AttachTask(CompressionTaskViewModel task, Attachment? owner)
    {
        var window = owner ?? Default;
        window?.Jobs.Add(task.JobId);
        window?.SinkCallback?.Invoke(task);
    }
    // 只通知持有该任务的窗口；未登记的任务退回第一个窗口，保持单窗口行为。
    private void MirrorTask(CompressionTaskViewModel task) => (OwnerOf(task.JobId) ?? Default)?.MirrorCallback?.Invoke(task);
    private void NotifyTasks(CompressionTaskViewModel? task = null)
    {
        var window = task is null ? null : OwnerOf(task.JobId) ?? Default;
        if (window is not null) { window.TasksChangedCallback?.Invoke(); return; }
        foreach (var item in Live) item.TasksChangedCallback?.Invoke();
    }
    private void NotifyDerived(CompressionTaskViewModel? task = null)
    {
        var window = task is null ? null : OwnerOf(task.JobId) ?? Default;
        if (window is not null) { window.DerivedChangedCallback?.Invoke(); return; }
        foreach (var item in Live) item.DerivedChangedCallback?.Invoke();
    }
    private void Report(string notice) => Default?.ReportCallback?.Invoke(notice);

    // ---- 权威配置：单一真相源，多个窗口共享 ----
    public AppConfig Config { get; private set; } = new();
    /// <summary>由窗口初始化时写入从磁盘读出的配置；不落盘。</summary>
    public void AdoptConfig(AppConfig config)
    {
        Config = config;
        foreach (var window in Live) window.ConfigChangedCallback?.Invoke();
    }
    /// <summary>读-改-写：避免两个窗口各持副本、后写者回滚前者。</summary>
    public async Task UpdateConfigAsync(Func<AppConfig, AppConfig> mutate, Func<AppConfig, Task> persist)
    {
        await configGate.WaitAsync();
        try
        {
            var updated = mutate(Config);
            await persist(updated);
            Config = updated;
        }
        finally { configGate.Release(); }
        foreach (var window in Live) window.ConfigChangedCallback?.Invoke();
    }

    public ObservableCollection<CompressionTaskViewModel> Tasks { get; } = [];
    public bool HasUnfinishedTasks => Tasks.Any(t => !t.TerminalConfirmed);
    public CompressionTaskViewModel? Get(string jobId) => taskIndex.TryGetValue(jobId, out var task) ? task : null;
    public bool TasksReady { get; private set; }
    public void MarkReady() => TasksReady = true;
    public int NasCores { get; private set; }
    public long NasMemoryMb { get; private set; }
    public void SetNasProbeResult(int cores, long memoryMb) { NasCores = cores; NasMemoryMb = memoryMb; }

    public int EffectivePcConcurrency =>
        Config.PcConcurrency is > 0 and <= 64 ? Config.PcConcurrency : Math.Max(1, Environment.ProcessorCount);
    public int EffectiveNasConcurrency
    {
        get
        {
            var hardwareLimit = NasCores > 0 ? Math.Min(NasCores, 4) : 4;
            return Math.Clamp(Config.NasConcurrency, 1, hardwareLimit);
        }
    }

    // All lifecycle transitions, reservations and durable writes share one gate on the UI dispatcher.
    private Task UpdateTasksAsync(Func<Task> update)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(async () =>
        {
            try
            {
                await taskGate.WaitAsync();
                try { await update(); }
                finally { taskGate.Release(); }
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private TaskRecord Record(CompressionTaskViewModel task, string phase) => new()
    {
        Request = task.Request, Phase = phase, CreatedAt = task.CreatedAt,
        Result = task.FinalResult, Failures = task.Failures.ToArray()
    };

    /// <summary>恢复只跑一次：多个窗口并发初始化时共享同一个 Task，避免同一 JobId 复活出两个实例。</summary>
    public Task EnsureRecoveredAsync() => recovery ??= RecoverTasksAsync();
    private async Task RecoverTasksAsync()
    {
        await UpdateTasksAsync(async () =>
        {
            TaskRecovery result;
            try { result = await taskStore.LoadAsync(); }
            catch (Exception ex) { Report($"任务记录无法读取：{ex.Message}"); return; }
            foreach (var record in result.Records)
            {
                // 已在池中的记录不重复登记：再次恢复同一 JobId 会造成两个实例各自收事件、各自收尾。
                if (taskIndex.ContainsKey(record.Request.JobId)) continue;
                var task = new CompressionTaskViewModel(record.Request, record.Request.Engine == "nas" ? 1 : 0, record.CreatedAt)
                {
                    PersistedPhase = record.Phase,
                    State = record.Phase == "queued" ? "awaitingResume" : "unknown",
                    OwnsSlot = record.Phase is "launching" or "running",
                    RunDispatched = record.Phase is "launching" or "running",
                    Message = record.Phase == "queued" ? "此任务尚未启动，可继续或移除。" : "上次任务尚未确认结束，请重新检查。",
                    ResultSummary = record.Phase == "queued" ? "等待手动继续" : "状态待确认"
                };
                foreach (var failure in record.Failures) task.Failures.Add(failure);
                taskIndex[task.JobId] = task; Tasks.Add(task); AttachTask(task, null);
                if (record.Phase == "finished" && record.Result is { } terminal)
                {
                    task.Apply(terminal); MirrorTask(task); await FinalizeTaskAsync(task);
                }
            }
            if (result.Errors.Count > 0) Report(string.Join("\n\n", result.Errors));
            NotifyTasks();
        });
    }
    public async Task<CompressionTaskViewModel?> StartAsync(Attachment? owner, WorkerRequest request, int engineIndex)
    {
        CompressionTaskViewModel? task = null;
        await UpdateTasksAsync(async () =>
        {
            if (TryFocusDuplicate(request.Sources)) return;
            task = new(request, engineIndex);
            taskIndex[task.JobId] = task; Tasks.Insert(0, task); AttachTask(task, owner);
            try { await taskStore.SaveAsync(Record(task, "queued")); }
            catch (Exception ex) { await FailBeforeStartAsync(task, ex); return; }
            Report("");
            // Schedule without running reentrantly while this transition owns the gate.
            task.Execution = RunTaskAsync(task);
            NotifyDerived(task); NotifyTasks(task);
        });
        return task;
    }
    public async Task ResumeTaskAsync(CompressionTaskViewModel task)
    {
        await UpdateTasksAsync(() =>
        {
            if (task.CanResume && taskIndex.ContainsKey(task.JobId))
            {
                task.State = "queued"; task.Message = ""; task.ResultSummary = "排队等待执行";
                MirrorTask(task); task.Execution = RunTaskAsync(task); NotifyTasks(task);
            }
            return Task.CompletedTask;
        });
    }
    private bool TryFocusDuplicate(string[] requested)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var existing = Tasks.FirstOrDefault(t => !t.TerminalConfirmed
            && t.Request.Sources.Select(Path.GetFullPath).OrderBy(p => p, comparer)
                .SequenceEqual(requested.Select(Path.GetFullPath).OrderBy(p => p, comparer), comparer));
        if (existing is null) return false;
        Report($"该来源已在任务列表中（{existing.StateTitle}），未重复加入。");
        return true;
    }

    private async Task<bool> WaitForSlotAsync(CompressionTaskViewModel task)
    {
        while (!task.QueueCancellation.IsCancellationRequested)
        {
            var ready = false;
            await UpdateTasksAsync(async () =>
            {
                if (task.TerminalConfirmed || task.QueueCancellation.IsCancellationRequested || !taskIndex.ContainsKey(task.JobId)) return;
                var occupied = taskIndex.Values.Count(t => t.EngineIndex == task.EngineIndex && t.OwnsSlot);
                var limit = task.EngineIndex == 1 ? 1 : EffectivePcConcurrency;
                var olderQueued = taskIndex.Values.Any(t => t != task && t.EngineIndex == task.EngineIndex && t.IsQueued
                    && !t.QueueCancellation.IsCancellationRequested && t.CreatedAt < task.CreatedAt);
                if (occupied >= limit || olderQueued) return;
                task.OwnsSlot = true;
                var cores = Math.Clamp(Environment.ProcessorCount, 1, 64);
                var activePc = taskIndex.Values.Count(t => t.OwnsSlot && t.EngineIndex == 0);
                var threads = task.EngineIndex == 1
                    ? EffectiveNasConcurrency
                    : Math.Max(1, cores / Math.Min(Math.Max(activePc, 1), EffectivePcConcurrency));
                task.Request = task.Request with { Options = task.Request.Options with { Threads = threads } };
                var hardware = Math.Min(NasCores > 0 ? NasCores : 4, 4);
                var detected = NasCores > 0 ? $"NAS {NasCores} 核" : "NAS 核数待探测";
                task.ConcurrencyText = task.EngineIndex == 1
                    ? $"NAS 并发 {threads}（上限 {hardware}，{detected}）"
                    : $"PC 任务并发 {threads}";
                task.State = "preparing";
                try
                {
                    await taskStore.SaveAsync(Record(task, "launching"));
                    task.PersistedPhase = "launching";
                    ready = true;
                }
                catch (Exception ex) { await FailBeforeStartAsync(task, ex); }
                MirrorTask(task);
            });
            if (ready) return true;
            if (task.TerminalConfirmed) return false;
            try { await Task.Delay(150, task.QueueCancellation.Token); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }
    private async Task RunTaskAsync(CompressionTaskViewModel task)
    {
        if (!await WaitForSlotAsync(task)) return;
        var launch = false;
        await UpdateTasksAsync(() =>
        {
            // A removal/close request can win after reservation or its durable write.
            if (!task.TerminalConfirmed && !task.QueueCancellation.IsCancellationRequested)
            { task.RunDispatched = true; launch = true; }
            return Task.CompletedTask;
        });
        if (launch) await ObserveAsync(task, "run");
    }
    private async Task ObserveAsync(CompressionTaskViewModel task, string operation)
    {
        var pending = new List<Task>();
        Exception? failure = null;
        try
        {
            await worker.ExecuteAsync(task.Request with { Operation = operation }, e =>
                pending.Add(UpdateTasksAsync(() => HandleEventAsync(task, e))));
        }
        catch (Exception ex) { failure = ex; }
        // Worker completion is not UI-dispatch completion. Drain every applied event first.
        try { await Task.WhenAll(pending); }
        catch (Exception ex) { failure ??= ex; }
        if (failure is not null)
        {
            await UpdateTasksAsync(() =>
            {
                if (!task.TerminalConfirmed)
                {
                    task.State = "unknown"; task.Message = failure.Message;
                    // The process may still be running. Its reservation is deliberately retained.
                }
                MirrorTask(task); return Task.CompletedTask;
            });
        }
    }
    private async Task HandleEventAsync(CompressionTaskViewModel task, WorkerEvent e)
    {
        if (task.TerminalConfirmed || e.JobId != task.JobId) return;
        task.Apply(e);
        if (task.TerminalConfirmed) await FinalizeTaskAsync(task);
        else if (e.Type is "phase" or "manifest" or "file" && task.PersistedPhase == "launching")
        {
            try { await taskStore.SaveAsync(Record(task, "running")); task.PersistedPhase = "running"; }
            catch (Exception ex) { task.Message = $"运行状态无法保存，启动记录已保留：{ex.Message}"; }
        }
        MirrorTask(task);
    }
    private async Task FailBeforeStartAsync(CompressionTaskViewModel task, Exception error)
    {
        task.QueueCancellation.Cancel();
        task.Apply(new() { JobId = task.JobId, Type = "finished", State = "failed", Message = $"任务未启动，记录无法保存：{error.Message}" });
        task.ResultSummary = "未调用压缩引擎";
        await FinalizeTaskAsync(task); MirrorTask(task);
    }
    // Called under taskGate only; persistence and deletion must finish before considering cleanup done.
    private async Task FinalizeTaskAsync(CompressionTaskViewModel task, bool remove = false)
    {
        if (!task.TerminalConfirmed || task.Finalized) return;
        task.OwnsSlot = false;
        var saved = false;
        try { await taskStore.SaveAsync(Record(task, "finished")); task.PersistedPhase = "finished"; saved = true; }
        catch (Exception ex) { task.Message += $"\n终态无法保存：{ex.Message}"; }
        try
        {
            await taskStore.DeleteAsync(task.JobId);
            task.Finalized = true;
        }
        catch (Exception ex)
        {
            task.Message += $"\n任务记录清理失败{(saved ? "，下次启动会按终态恢复" : "，请保留日志重新检查")}：{ex.Message}";
        }
        if (task.Finalized && (remove || task.State is "succeeded" or "dryRun" or "emptyResult"))
        { Tasks.Remove(task); taskIndex.Remove(task.JobId); }
        NotifyTasks(task);
    }
    public async Task CancelTaskAsync(CompressionTaskViewModel task)
    {
        var sendCancel = false;
        await UpdateTasksAsync(async () =>
        {
            if (task.TerminalConfirmed) return;
            if (!task.RunDispatched)
            {
                task.QueueCancellation.Cancel();
                task.Apply(new() { JobId = task.JobId, Type = "finished", State = "cancelled", Message = "已移除，未启动压缩任务。" });
                await FinalizeTaskAsync(task, remove: true); MirrorTask(task);
            }
            else if (task.CanCancel)
            { task.State = "cancelling"; sendCancel = true; MirrorTask(task); }
        });
        if (sendCancel) await ObserveAsync(task, "cancel");
    }
    public async Task InspectTaskAsync(CompressionTaskViewModel task)
    {
        var inspect = false;
        await UpdateTasksAsync(() =>
        {
            if (task.CanInspect) { task.Checking = true; inspect = true; NotifyTasks(task); }
            return Task.CompletedTask;
        });
        if (!inspect) return;
        try { await ObserveAsync(task, "inspect"); }
        finally { await UpdateTasksAsync(() => { task.Checking = false; NotifyTasks(task); return Task.CompletedTask; }); }
    }
    public async Task CancelAllAndWaitAsync()
    {
        CompressionTaskViewModel[] snapshot = [];
        await UpdateTasksAsync(() =>
        {
            snapshot = Tasks.Where(t => !t.TerminalConfirmed).ToArray();
            // Stop ALL queued waiters before cancellation of another task can release a slot.
            foreach (var task in snapshot.Where(t => !t.RunDispatched)) task.QueueCancellation.Cancel();
            return Task.CompletedTask;
        });
        await Task.WhenAll(snapshot.Select(t => t.IsUnknown ? InspectTaskAsync(t) : CancelTaskAsync(t)));
        await Task.WhenAll(snapshot.Select(t => t.Execution ?? Task.CompletedTask));
    }
    public void Apply(WorkerEvent e) => _ = UpdateTasksAsync(async () =>
    {
        if (taskIndex.TryGetValue(e.JobId, out var task)) await HandleEventAsync(task, e);
    });
}
