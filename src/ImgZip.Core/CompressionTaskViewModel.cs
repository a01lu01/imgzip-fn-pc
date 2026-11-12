using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImgZip.Core;

/// <summary>单个压缩任务；多任务并行时每个任务一个实例，事件按其 JobId 路由到对应实例。</summary>
public partial class CompressionTaskViewModel : ObservableObject
{
    public CompressionTaskViewModel(WorkerRequest request, int engineIndex, DateTimeOffset? createdAt = null)
    {
        Request = request;
        EngineIndex = engineIndex;
        EngineName = engineIndex == 1 ? "NAS" : "本机 PC";
        SourceTitle = request.Sources.Length > 1
            ? $"{request.Sources.Length} 张图片"
            : Path.GetFileName(request.Sources[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(SourceTitle)) SourceTitle = string.Join("; ", request.Sources);
        CreatedAt = createdAt ?? DateTimeOffset.Now;
        ConcurrencyText = engineIndex == 1
            ? $"NAS 并发 {Math.Min(Math.Max(request.Options.Threads, 1), 4)}（硬上限 4）"
            : $"PC 任务并发 {request.Options.Threads}";
        Failures.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFailures));
    }
    public WorkerRequest Request { get; internal set; }
    public int EngineIndex { get; }
    public string EngineName { get; }
    public string SourceTitle { get; }
    public DateTimeOffset CreatedAt { get; }
    public string JobId => Request.JobId;
    public ObservableCollection<string> Failures { get; } = [];
    /// <summary>终态已确认；此后忽略迟到的 unknown 等事件（与既有单任务语义一致）。</summary>
    public bool TerminalConfirmed { get; internal set; }
    /// <summary>终态收尾（删任务记录、成功则移出列表）是否已执行，保证幂等。</summary>
    public bool Finalized { get; internal set; }
    internal CancellationTokenSource QueueCancellation { get; } = new();
    internal bool OwnsSlot { get; set; }
    internal bool RunDispatched { get; set; }
    internal string PersistedPhase { get; set; } = "queued";
    internal Task? Execution { get; set; }
    internal WorkerEvent? FinalResult { get; set; }
    /// <summary>执行层进程 ID；0 表示未记录。用于强制关闭与重启时的存活判定。</summary>
    internal int WorkerPid { get; set; }
    internal DateTimeOffset WorkerStartedAt { get; set; }

    [ObservableProperty] private string state = "queued";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private int total;
    [ObservableProperty] private int completed;
    [ObservableProperty] private string currentFile = "";
    [ObservableProperty] private string outputPath = "等待开始";
    [ObservableProperty] private string openOutputPath = "";
    [ObservableProperty] private string resultSummary = "排队等待执行";
    [ObservableProperty] private bool checking;
    [ObservableProperty] private string concurrencyText = "";
    [ObservableProperty] private int copyTotal;
    [ObservableProperty] private int copyCompleted;
    [ObservableProperty] private long copiedBytes;

    public bool IsQueued => State == "queued";
    /// <summary>
    /// 排队原因，例如「NAS 已有任务在执行」。排队时才有值，拿到名额后清空。
    /// 只显示「排队中」而不说在等谁，用户无法判断要等多久，也不知道是不是卡住了。
    /// </summary>
    public string QueueReason
    {
        get => queueReason;
        internal set
        {
            if (queueReason == value) return;
            queueReason = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueueReason));
        }
    }
    private string queueReason = "";
    public bool HasQueueReason => queueReason.Length > 0;
    public bool CanResume => State == "awaitingResume";
    public bool CanRemove => IsQueued || CanResume;
    public bool IsRunning => State is "preparing" or "running" or "cancelling";
    public bool IsUnknown => State == "unknown";
    public bool IsTerminal => State is "succeeded" or "partial" or "failed" or "cancelled" or "dryRun" or "emptyResult";
    public bool CanCancel => State is "preparing" or "running";
    public bool CanInspect => State == "unknown" && !Checking;
    public bool CanOpenOutput => !IsRunning && State is "succeeded" or "partial" && !string.IsNullOrEmpty(OpenOutputPath);
    public bool HasFailures => Failures.Count > 0;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool IsIndeterminate => State == "preparing" || (IsRunning && Total == 0);
    public double Progress => Total == 0 ? 0 : 100d * Completed / Total;
    public string ProgressText => Total == 0 ? "" : $"{Completed} / {Total}";
    public bool HasCopyProgress => CopyTotal > 0;
    public string CopyProgressText => CopyTotal == 0 ? "" : $"其他文件 {CopyCompleted} / {CopyTotal} · {Display.Bytes(CopiedBytes)}";
    public string StateTitle => State switch
    {
        "queued" => "排队中", "awaitingResume" => "等待手动继续", "preparing" => "正在准备…", "running" => "正在压缩…", "cancelling" => "正在取消…",
        "cancelled" => "已取消", "succeeded" => "已完成", "partial" => "部分未完成", "failed" => "未完成",
        "dryRun" => "预演完成", "emptyResult" => "没有可压缩的图片", "unknown" => "状态待确认", _ => State
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(State) or nameof(Total) or nameof(Completed) or nameof(Checking) or nameof(CopyTotal) or nameof(CopyCompleted) or nameof(CopiedBytes))
            foreach (var name in new[] { nameof(IsQueued), nameof(CanResume), nameof(CanRemove), nameof(IsRunning), nameof(IsUnknown), nameof(IsTerminal), nameof(CanCancel), nameof(CanInspect), nameof(CanOpenOutput), nameof(IsIndeterminate), nameof(Progress), nameof(ProgressText), nameof(StateTitle), nameof(HasCopyProgress), nameof(CopyProgressText) })
                base.OnPropertyChanged(new PropertyChangedEventArgs(name));
        // 排队状态一变，排队原因就该跟着出现或消失。
        if (e.PropertyName == nameof(State)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasQueueReason)));
        if (e.PropertyName == nameof(OpenOutputPath)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanOpenOutput)));
        if (e.PropertyName == nameof(Message)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasMessage)));
    }

    public void Apply(WorkerEvent e)
    {
        if (e.JobId != JobId || TerminalConfirmed) return;
        if (e.Type == "phase")
        {
            if (State is "queued" or "awaitingResume" or "preparing") State = "preparing";
            if (!string.IsNullOrWhiteSpace(e.Message))
            {
                Message = e.Message;
                if (e.Message.Contains("NAS 并发", StringComparison.Ordinal) || e.Message.Contains("NAS 可用内存", StringComparison.Ordinal))
                    resourceNote = e.Message;
            }
        }
        if (e.Type is "manifest" or "file" or "copy")
        {
            Total = e.Total; Completed = e.Completed;
            CopyTotal = e.CopyTotal; CopyCompleted = e.CopyCompleted; CopiedBytes = e.CopiedBytes;
            if (!string.IsNullOrEmpty(e.Output)) OutputPath = e.Output;
            if (State != "cancelling") State = "running";
        }
        if (e.Type == "file")
        {
            CurrentFile = e.Path;
            if (e.State == "failed")
            {
                var error = $"{e.Path}\n{e.Message}";
                if (!Failures.Contains(error)) Failures.Add(error);
            }
        }
        if (e.Type == "copy")
        {
            CurrentFile = e.Path;
            if (e.State == "failed")
            {
                var error = $"{e.Path}\n复制失败：{e.Message}";
                if (!Failures.Contains(error)) Failures.Add(error);
            }
        }
        if (e.Type == "finished")
        {
            State = e.State == "empty" ? "emptyResult" : e.State;
            Total = e.Total; Completed = e.Completed;
            CopyTotal = e.CopyTotal; CopyCompleted = e.CopyCompleted; CopiedBytes = e.CopiedBytes;
            if (!string.IsNullOrEmpty(e.Output)) OutputPath = e.Output;
            OpenOutputPath = Request.Engine == "pc" ? e.Output : e.OpenOutput;
            ResultSummary = Display.Summary(e);
            Message = string.IsNullOrWhiteSpace(resourceNote) ? e.Message : $"{e.Message}\n{resourceNote}".Trim();
            if (State != "unknown") { TerminalConfirmed = true; FinalResult = e; }
        }
        if (e.Type == "error") Message = e.Message;
    }

    private string resourceNote = "";
}
