namespace ImgZip.Core;

public partial class MainViewModel
{
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
    private async Task RecoverTasksAsync()
    {
        await UpdateTasksAsync(async () =>
        {
            TaskRecovery recovery;
            try { recovery = await taskStore.LoadAsync(); }
            catch (Exception ex) { Message = $"任务记录无法读取：{ex.Message}"; return; }
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
                taskIndex[task.JobId] = task; Tasks.Add(task); SetPrimary(task);
                if (record.Phase == "finished" && record.Result is { } result)
                {
                    task.Apply(result); Mirror(task); await FinalizeTaskAsync(task);
                }
            }
            if (recovery.Errors.Count > 0) Message = string.Join("\n\n", recovery.Errors);
            NotifyTasks();
        });
    }
    public async Task StartAsync()
    {
        if (!CanStartTask) return;
        CompressionTaskViewModel? task = null;
        try
        {
            var options = BuildOptions();
            if (options.Validate() is { } validation) throw new ArgumentException(validation);
            if (EngineIndex == 1 && Config.ValidateConnection() is { } error) throw new ArgumentException(error);
            var request = Request("run") with { Options = options };
            await UpdateTasksAsync(async () =>
            {
                if (TryFocusDuplicate(request.Sources)) return;
                task = new(request, request.Engine == "nas" ? 1 : 0);
                taskIndex[task.JobId] = task; Tasks.Insert(0, task); SetPrimary(task);
                try { await taskStore.SaveAsync(Record(task, "queued")); }
                catch (Exception ex) { await FailBeforeStartAsync(task, ex); return; }
                Message = "";
                // Schedule without running reentrantly while this transition owns the gate.
                task.Execution = RunTaskAsync(task);
                NotifyDerived(); NotifyTasks();
            });
            if (task?.Execution is { } execution) await execution;
        }
        catch (Exception ex) { Message = ex.Message; }
    }
    public async Task ResumeTaskAsync(CompressionTaskViewModel task)
    {
        await UpdateTasksAsync(() =>
        {
            if (task.CanResume && taskIndex.ContainsKey(task.JobId))
            {
                task.State = "queued"; task.Message = ""; task.ResultSummary = "排队等待执行";
                SetPrimary(task); task.Execution = RunTaskAsync(task); NotifyTasks();
            }
            return Task.CompletedTask;
        });
        if (task.Execution is { } execution) await execution;
    }
    private bool TryFocusDuplicate(string[] requested)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var existing = Tasks.FirstOrDefault(t => !t.TerminalConfirmed
            && t.Request.Sources.Select(Path.GetFullPath).OrderBy(p => p, comparer)
                .SequenceEqual(requested.Select(Path.GetFullPath).OrderBy(p => p, comparer), comparer));
        if (existing is null) return false;
        Message = $"该来源已在任务列表中（{existing.StateTitle}），未重复加入。";
        return true;
    }
    private void SetPrimary(CompressionTaskViewModel task)
    {
        primaryRequest = task.Request; Mirror(task);
    }
    private void Mirror(CompressionTaskViewModel task)
    {
        if (primaryRequest?.JobId == task.JobId)
        {
            State = task.State; Total = task.Total; Completed = task.Completed; CurrentFile = task.CurrentFile;
            Message = task.Message; ResultSummary = task.ResultSummary; OutputPath = task.OutputPath; OpenOutputPath = task.OpenOutputPath;
            Failures.Clear(); foreach (var failure in task.Failures) Failures.Add(failure);
            NotifyDerived();
        }
        NotifyTasks();
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
                var active = taskIndex.Values.Count(t => t.OwnsSlot);
                task.Request = task.Request with { Options = task.Request.Options with { Threads = Math.Max(1, cores / Math.Min(active, cores)) } };
                task.State = "preparing";
                try
                {
                    await taskStore.SaveAsync(Record(task, "launching"));
                    task.PersistedPhase = "launching";
                    ready = true;
                }
                catch (Exception ex) { await FailBeforeStartAsync(task, ex); }
                Mirror(task);
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
                Mirror(task); return Task.CompletedTask;
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
        Mirror(task);
    }
    private async Task FailBeforeStartAsync(CompressionTaskViewModel task, Exception error)
    {
        task.QueueCancellation.Cancel();
        task.Apply(new() { JobId = task.JobId, Type = "finished", State = "failed", Message = $"任务未启动，记录无法保存：{error.Message}" });
        task.ResultSummary = "未调用压缩引擎";
        await FinalizeTaskAsync(task); Mirror(task);
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
        NotifyTasks();
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
                await FinalizeTaskAsync(task, remove: true); Mirror(task);
            }
            else if (task.CanCancel)
            { task.State = "cancelling"; sendCancel = true; Mirror(task); }
        });
        if (sendCancel) await ObserveAsync(task, "cancel");
    }
    public async Task InspectTaskAsync(CompressionTaskViewModel task)
    {
        var inspect = false;
        await UpdateTasksAsync(() =>
        {
            if (task.CanInspect) { task.Checking = true; inspect = true; NotifyTasks(); }
            return Task.CompletedTask;
        });
        if (!inspect) return;
        try { await ObserveAsync(task, "inspect"); }
        finally { await UpdateTasksAsync(() => { task.Checking = false; NotifyTasks(); return Task.CompletedTask; }); }
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
    private CompressionTaskViewModel? PrimaryTask() =>
        primaryRequest is not null && taskIndex.TryGetValue(primaryRequest.JobId, out var task) ? task : null;
    public Task CancelAsync() => PrimaryTask() is { } task ? CancelTaskAsync(task) : Task.CompletedTask;
    public Task InspectAsync() => PrimaryTask() is { } task ? InspectTaskAsync(task) : Task.CompletedTask;
    public void Apply(WorkerEvent e) => _ = UpdateTasksAsync(async () =>
    {
        if (taskIndex.TryGetValue(e.JobId, out var task)) await HandleEventAsync(task, e);
    });
}
