namespace ImgZip.Core;

public partial class MainViewModel
{
    // 阶段 1a：生命周期全部移入 TaskCoordinator，这里只保留面向窗口的门面。
    public Task StartAsync()
    {
        if (!CanStartTask) return Task.CompletedTask;
        return StartCoreAsync();
    }
    private async Task StartCoreAsync()
    {
        CompressionTaskViewModel? task = null;
        try
        {
            var options = BuildOptions();
            if (options.Validate() is { } validation) throw new ArgumentException(validation);
            if (EngineIndex == 1 && Config.ValidateConnection() is { } error) throw new ArgumentException(error);
            var request = Request("run") with { Options = options };
            task = await Coordinator.StartAsync(Attachment, request, EngineIndex == 1 ? 1 : 0);
            if (task is null) return;
        }
        catch (Exception ex) { Message = ex.Message; return; }
        if (task.Execution is { } execution) await execution;
    }
    public async Task ResumeTaskAsync(CompressionTaskViewModel task)
    {
        await Coordinator.ResumeTaskAsync(task);
        if (task.Execution is { } execution) await execution;
    }
    private void SetPrimary(CompressionTaskViewModel task)
    {
        primaryRequest = task.Request; CurrentTask = task; Mirror(task);
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

    public async Task CancelTaskAsync(CompressionTaskViewModel task)
    {
        await Coordinator.CancelTaskAsync(task);
        Mirror(task);
    }
    public async Task InspectTaskAsync(CompressionTaskViewModel task)
    {
        try { await Coordinator.InspectTaskAsync(task); }
        finally { Mirror(task); }
    }
    public async Task CancelAllAndWaitAsync()
    {
        await Coordinator.CancelAllAndWaitAsync();
        if (PrimaryTask() is { } task) Mirror(task);
    }
    /// <summary>
    /// 强制关闭本窗口的任务：杀进程、清暂存、写终态。
    /// 窗口只结束自己那一个任务，别的窗口的任务不受影响。
    /// </summary>
    public async Task<ForceCloseReport> ForceCloseAsync()
    {
        var report = await Coordinator.ForceCloseAsync(CurrentTask?.JobId);
        if (report.Terminated > 0) Message = "";
        if (report.Unconfirmed > 0)
            Message = $"{report.Unconfirmed} 个任务的进程未能确认停止；记录已保留，下次启动会自动判定。";
        NotifyTasks();
        return report;
    }
    private CompressionTaskViewModel? PrimaryTask() =>
        primaryRequest is not null ? Coordinator.Get(primaryRequest.JobId) : null;
    public Task CancelAsync() => PrimaryTask() is { } task ? CancelTaskAsync(task) : Task.CompletedTask;
    public Task InspectAsync() => PrimaryTask() is { } task ? InspectTaskAsync(task) : Task.CompletedTask;
    public void Apply(WorkerEvent e) => Coordinator.Apply(e);
}
