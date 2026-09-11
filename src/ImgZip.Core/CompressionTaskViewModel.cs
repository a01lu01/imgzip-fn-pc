using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImgZip.Core;

/// <summary>单个压缩任务；多任务并行时每个任务一个实例，事件按其 JobId 路由到对应实例。</summary>
public partial class CompressionTaskViewModel : ObservableObject
{
    public CompressionTaskViewModel(WorkerRequest request, int engineIndex)
    {
        Request = request;
        EngineIndex = engineIndex;
        EngineName = engineIndex == 1 ? "NAS" : "本机 PC";
        SourceTitle = request.Sources.Length > 1
            ? $"{request.Sources.Length} 张图片"
            : Path.GetFileName(request.Sources[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(SourceTitle)) SourceTitle = string.Join("; ", request.Sources);
        CreatedAt = DateTimeOffset.Now;
    }
    public WorkerRequest Request { get; set; }
    public int EngineIndex { get; }
    public string EngineName { get; }
    public string SourceTitle { get; }
    public DateTimeOffset CreatedAt { get; }
    public string JobId => Request.JobId;
    public ObservableCollection<string> Failures { get; } = [];
    /// <summary>终态已确认；此后忽略迟到的 unknown 等事件（与既有单任务语义一致）。</summary>
    public bool TerminalConfirmed { get; set; }
    /// <summary>终态收尾（删任务记录、成功则移出列表）是否已执行，保证幂等。</summary>
    public bool Finalized { get; set; }

    [ObservableProperty] private string state = "queued";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private int total;
    [ObservableProperty] private int completed;
    [ObservableProperty] private string currentFile = "";
    [ObservableProperty] private string outputPath = "等待开始";
    [ObservableProperty] private string openOutputPath = "";
    [ObservableProperty] private string resultSummary = "排队等待执行";
    [ObservableProperty] private bool checking;

    public bool IsQueued => State == "queued";
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
    public string StateTitle => State switch
    {
        "queued" => "排队中", "preparing" => "正在准备…", "running" => "正在压缩…", "cancelling" => "正在取消…",
        "cancelled" => "已取消", "succeeded" => "已完成", "partial" => "部分未完成", "failed" => "未完成",
        "dryRun" => "预演完成", "emptyResult" => "没有可压缩的图片", "unknown" => "状态待确认", _ => State
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(State) or nameof(Total) or nameof(Completed) or nameof(Checking))
            foreach (var name in new[] { nameof(IsQueued), nameof(IsRunning), nameof(IsUnknown), nameof(IsTerminal), nameof(CanCancel), nameof(CanInspect), nameof(CanOpenOutput), nameof(IsIndeterminate), nameof(Progress), nameof(ProgressText), nameof(StateTitle) })
                base.OnPropertyChanged(new PropertyChangedEventArgs(name));
    }

    public void Apply(WorkerEvent e)
    {
        if (e.JobId != JobId || TerminalConfirmed) return;
        if (e.Type == "phase" && State != "cancelling") State = "preparing";
        if (e.Type is "manifest" or "file")
        {
            Total = e.Total; Completed = e.Completed;
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
        if (e.Type == "finished")
        {
            State = e.State == "empty" ? "emptyResult" : e.State;
            Total = e.Total; Completed = e.Completed;
            if (!string.IsNullOrEmpty(e.Output)) OutputPath = e.Output;
            OpenOutputPath = e.OpenOutput;
            ResultSummary = Display.Summary(e);
            Message = e.Message;
            if (State != "unknown") TerminalConfirmed = true;
        }
        if (e.Type == "error") Message = e.Message;
    }
}
