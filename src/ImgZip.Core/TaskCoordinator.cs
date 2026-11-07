using System.Collections.ObjectModel;

namespace ImgZip.Core;

/// <summary>
/// 应用级任务协调者：持有全部任务、名额分配与持久化写入。
/// 阶段 1a 中仍是实例级——每个 <see cref="MainViewModel"/> 自建一个，行为与拆分前逐字节一致。
/// 窗口相关的一切（primaryRequest、镜像、草稿）仍留在 MainViewModel，通过下面的委托回调。
/// </summary>
public sealed class TaskCoordinator
{
    private readonly IWorkerClient worker;
    private readonly ITaskStore taskStore;
    private readonly Action<Action> dispatch;
    private readonly Dictionary<string, CompressionTaskViewModel> taskIndex = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim taskGate = new(1, 1);

    public TaskCoordinator(IWorkerClient worker, ITaskStore taskStore, Action<Action>? dispatch = null)
    {
        this.worker = worker; this.taskStore = taskStore;
        this.dispatch = dispatch ?? (action => action());
    }

    // ---- 窗口回调：由 MainViewModel 构造时接上，单窗口时与拆分前等价 ----
    /// <summary>把某个任务设为该窗口的主任务（原 SetPrimary）。</summary>
    public Action<CompressionTaskViewModel>? Sink { get; set; }
    /// <summary>把主任务状态刷到窗口绑定属性（原 Mirror）。</summary>
    public Action<CompressionTaskViewModel>? Mirror { get; set; }
    public Action? NotifyTasks { get; set; }
    public Action? NotifyDerived { get; set; }
    /// <summary>向窗口写一条提示（原直接写 Message）。</summary>
    public Action<string>? Report { get; set; }
    /// <summary>读取该窗口当前的配置快照，用于计算并发限额。</summary>
    public Func<AppConfig>? ConfigAccessor { get; set; }

    public ObservableCollection<CompressionTaskViewModel> Tasks { get; } = [];
    public bool HasUnfinishedTasks => Tasks.Any(t => !t.TerminalConfirmed);
    public CompressionTaskViewModel? Get(string jobId) => taskIndex.TryGetValue(jobId, out var task) ? task : null;
    public bool TasksReady { get; private set; }
    public void MarkReady() => TasksReady = true;
    public int NasCores { get; private set; }
    public long NasMemoryMb { get; private set; }

    public int EffectivePcConcurrency
    {
        get
        {
            var config = ConfigAccessor?.Invoke() ?? new AppConfig();
            return config.PcConcurrency is > 0 and <= 64 ? config.PcConcurrency : Math.Max(1, Environment.ProcessorCount);
        }
    }
    public int EffectiveNasConcurrency
    {
        get
        {
            var config = ConfigAccessor?.Invoke() ?? new AppConfig();
            var hardwareLimit = NasCores > 0 ? Math.Min(NasCores, 4) : 4;
            return Math.Clamp(config.NasConcurrency, 1, hardwareLimit);
        }
    }
    public void SetNasProbeResult(int cores, long memoryMb) { NasCores = cores; NasMemoryMb = memoryMb; }

    // All lifecycle transitions, reservations and durable writes share one gate on the UI dispatcher.
    private Task UpdateTasksAsync(Func<Task> update)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatch(async () =>
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
    public async Task RecoverTasksAsync()
    {
        await UpdateTasksAsync(async () =>
        {
            TaskRecovery recovery;
            try { recovery = await taskStore.LoadAsync(); }
            catch (Exception ex) { Report?.Invoke($"任务记录无法读取：{ex.Message}"); return; }
            foreach (var record in recovery.Records)
            {
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
                taskIndex[task.JobId] = task; Tasks.Add(task); Sink?.Invoke(task);
                if (record.Phase == "finished" && record.Result is { } result)
                {
                    task.Apply(result); Mirror?.Invoke(task); await FinalizeTaskAsync(task);
                }
            }
            if (recovery.Errors.Count > 0) Report?.Invoke(string.Join("\n\n", recovery.Errors));
            NotifyTasks?.Invoke();
        });
    }
    public async Task<CompressionTaskViewModel?> StartAsync(WorkerRequest request, int engineIndex)
    {
        CompressionTaskViewModel? task = null;
        await UpdateTasksAsync(async () =>
        {
            if (TryFocusDuplicate(request.Sources)) return;
            task = new(request, engineIndex);
            taskIndex[task.JobId] = task; Tasks.Insert(0, task); Sink?.Invoke(task);
            try { await taskStore.SaveAsync(Record(task, "queued")); }
            catch (Exception ex) { await FailBeforeStartAsync(task, ex); return; }
            Report?.Invoke("");
            // Schedule without running reentrantly while this transition owns the gate.
            task.Execution = RunTaskAsync(task);
            NotifyDerived?.Invoke(); NotifyTasks?.Invoke();
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
                Sink?.Invoke(task); task.Execution = RunTaskAsync(task); NotifyTasks?.Invoke();
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
        Report?.Invoke($"该来源已在任务列表中（{existing.StateTitle}），未重复加入。");
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
                Mirror?.Invoke(task);
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
                Mirror?.Invoke(task); return Task.CompletedTask;
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
        Mirror?.Invoke(task);
    }
    private async Task FailBeforeStartAsync(CompressionTaskViewModel task, Exception error)
    {
        task.QueueCancellation.Cancel();
        task.Apply(new() { JobId = task.JobId, Type = "finished", State = "failed", Message = $"任务未启动，记录无法保存：{error.Message}" });
        task.ResultSummary = "未调用压缩引擎";
        await FinalizeTaskAsync(task); Mirror?.Invoke(task);
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
        NotifyTasks?.Invoke();
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
                await FinalizeTaskAsync(task, remove: true); Mirror?.Invoke(task);
            }
            else if (task.CanCancel)
            { task.State = "cancelling"; sendCancel = true; Mirror?.Invoke(task); }
        });
        if (sendCancel) await ObserveAsync(task, "cancel");
    }
    public async Task InspectTaskAsync(CompressionTaskViewModel task)
    {
        var inspect = false;
        await UpdateTasksAsync(() =>
        {
            if (task.CanInspect) { task.Checking = true; inspect = true; NotifyTasks?.Invoke(); }
            return Task.CompletedTask;
        });
        if (!inspect) return;
        try { await ObserveAsync(task, "inspect"); }
        finally { await UpdateTasksAsync(() => { task.Checking = false; NotifyTasks?.Invoke(); return Task.CompletedTask; }); }
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
