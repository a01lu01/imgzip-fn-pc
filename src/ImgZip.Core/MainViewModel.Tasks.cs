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
            // Message 与 ResultSummary 都不镜像：它们各有归属者。
            //   Message       —— 任务的进度与诊断，显示在任务卡片里；窗口这一层留给与任务无关的
            //                    提示（配置读不出、来源不合法、强制关闭清不干净）。
            //   ResultSummary —— 任务的真实结果摘要由任务卡片自述；窗口这一层是草稿的
            //                    「原始图片保持不变」，任务跑起来时把它清空反而丢信息。
            // 早先两条一起镜像，后果有两个：同一句「正在建立文件清单」在顶部 InfoBar 与任务卡片
            // 各出现一次；以及任务消息会顺手把窗口级的错误提示覆盖掉。
            OutputPath = task.OutputPath; OpenOutputPath = task.OpenOutputPath;
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
        var notes = new List<string>();
        if (report.Unconfirmed > 0)
            notes.Add($"{report.Unconfirmed} 个任务的进程未能确认停止；记录已保留，下次启动会自动判定。");
        // 进程杀掉了但暂存目录删不掉：半成品会一直躺在输出目录里，必须让用户知道。
        if (report.CleanupErrors.Count > 0)
            notes.Add($"有 {report.CleanupErrors.Count} 个半成品暂存目录无法删除，请手动清理输出目录。");
        if (notes.Count > 0) Message = string.Join("\n", notes);
        else if (report.Terminated > 0) Message = "";
        NotifyTasks();
        return report;
    }
    private CompressionTaskViewModel? PrimaryTask() =>
        primaryRequest is not null ? Coordinator.Get(primaryRequest.JobId) : null;
    public Task CancelAsync() => PrimaryTask() is { } task ? CancelTaskAsync(task) : Task.CompletedTask;
    public Task InspectAsync() => PrimaryTask() is { } task ? InspectTaskAsync(task) : Task.CompletedTask;
    public void Apply(WorkerEvent e) => Coordinator.Apply(e);
}
