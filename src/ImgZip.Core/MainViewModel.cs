using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImgZip.Core;

public partial class MainViewModel : ObservableObject
{
    private readonly IWorkerClient worker;
    private readonly ConfigStore store;
    private readonly string enginePath;
    private readonly Action<Action> dispatch;
    private readonly Dictionary<string, CompressionTaskViewModel> taskIndex = new(StringComparer.Ordinal);
    private WorkerRequest? primaryRequest;
    private string[] sources = [];
    private bool initializing = true;
    private bool hasConfigurationError;
    private int probeGeneration;
    private string PendingPath => Path.Combine(store.DirectoryPath, "active-job.json");
    private string TasksPath => Path.Combine(store.DirectoryPath, "tasks");

    [ObservableProperty] private AppConfig config = new();
    [ObservableProperty] private string state = "empty";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string sourceTitle = "将图片或文件夹拖到这里";
    [ObservableProperty] private string sourcePath = "支持 JPG、PNG、WebP、GIF";
    [ObservableProperty] private string sourceSummary = "添加来源后开始";
    [ObservableProperty] private string outputPath = "选择来源后自动生成";
    [ObservableProperty] private string openOutputPath = "";
    [ObservableProperty] private string resultSummary = "原始图片保持不变";
    [ObservableProperty] private string engineStatus = "正在检测本机引擎…";
    [ObservableProperty] private string currentFile = "";
    [ObservableProperty] private int engineIndex;
    [ObservableProperty] private int formatIndex = 1;
    [ObservableProperty] private int resizeIndex = 1;
    [ObservableProperty] private double quality = 90;
    [ObservableProperty] private double pixels = 4000;
    [ObservableProperty] private double maxSize;
    [ObservableProperty] private double threads = 4;
    [ObservableProperty] private bool lossless;
    [ObservableProperty] private bool noUpscale = true;
    [ObservableProperty] private bool recurse;
    [ObservableProperty] private bool dryRun;
    [ObservableProperty] private bool pcAvailable;
    [ObservableProperty] private bool advancedExpanded;
    [ObservableProperty] private bool checking;
    [ObservableProperty] private int total;
    [ObservableProperty] private int completed;
    [ObservableProperty] private string preset = "archive-jpeg";
    public ObservableCollection<string> Failures { get; } = [];
    public ObservableCollection<CompressionTaskViewModel> Tasks { get; } = [];

    public MainViewModel(IWorkerClient worker, ConfigStore store, string enginePath, Action<Action>? dispatch = null)
    {
        this.worker = worker; this.store = store; this.enginePath = enginePath;
        this.dispatch = dispatch ?? (action => action());
        initializing = false;
    }

    // ---- 草稿（新建任务）状态 ----
    public bool IsRunning => State is "preparing" or "running" or "cancelling";
    public bool IsLocked => IsRunning || State == "unknown";
    public bool IsUnknown => State == "unknown";
    public bool CanEdit => !IsLocked;
    public bool CanStart => CanEdit && sources.Length > 0 && (EngineIndex == 1 ? NasEligible : PcAvailable || DryRun);
    /// <summary>草稿是否可启动新任务：多任务下不再受“运行中”限制。</summary>
    public bool CanStartTask => sources.Length > 0 && (EngineIndex == 1 ? NasEligible : PcAvailable || DryRun);
    public bool CanCancel => State is "preparing" or "running";
    public bool CanInspect => State == "unknown" && !Checking;
    public bool CanOpenOutput => !IsLocked && State is "succeeded" or "partial" && !string.IsNullOrEmpty(OpenOutputPath);
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasFailures => Failures.Count > 0;
    public bool HasTasks => Tasks.Count > 0;
    public bool IsIndeterminate => State == "preparing" || (IsRunning && Total == 0);
    public bool CanSetQuality => !Lossless;
    public bool CanResize => ResizeIndex != 0;
    public bool NasEligible => !string.IsNullOrWhiteSpace(Config.Server) && sources.Length > 0 && sources.All(p => p.StartsWith(@"\\") || LooksLikeMappedDrive(p));
    public bool JpegSelected => Preset == "archive-jpeg";
    public bool WebpSelected => Preset == "archive-webp";
    public bool ShareSelected => Preset == "webp-share";
    public double Progress => Total == 0 ? 0 : 100d * Completed / Total;
    public string ProgressText => $"{Completed} / {Total}";
    public string StartText => DryRun ? "开始预演" : "开始压缩";
    public int EffectivePcConcurrency => Config.PcConcurrency is > 0 and <= 64 ? Config.PcConcurrency : Math.Max(1, Environment.ProcessorCount);
    public string TaskSummary
    {
        get
        {
            if (Tasks.Count == 0) return "暂无任务";
            var running = Tasks.Count(t => t.IsRunning);
            var queued = Tasks.Count(t => t.IsQueued);
            return $"{running} 个进行中 · {queued} 个排队 · 共 {Tasks.Count} 个任务（PC 并发 {EffectivePcConcurrency}）";
        }
    }
    public string StateTitle => State switch
    {
        "empty" => "添加图片即可开始", "ready" => "准备就绪", "preparing" => "正在准备…", "running" => DryRun ? "正在预演…" : "正在压缩…",
        "cancelling" => "正在取消…", "cancelled" => "任务已取消", "succeeded" => "压缩完成", "partial" => "部分图片未完成",
        "failed" => "任务未完成", "dryRun" => "预演完成", "unknown" => "任务状态尚未确认", "emptyResult" => "没有可压缩的图片", _ => State
    };
    public string AdvancedSummary => $"{(Lossless ? "无损" : $"{Quality:0} 质量")} · {(ResizeIndex == 0 ? "原始尺寸" : $"{Pixels:0} px")} · {(NoUpscale ? "不放大小图" : "允许放大")}";

    private static bool LooksLikeMappedDrive(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length < 3 || path[1] != ':') return false;
        try { return new DriveInfo(path[..3]).DriveType == DriveType.Network; } catch { return false; }
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (initializing) return;
        if (e.PropertyName is nameof(FormatIndex) or nameof(ResizeIndex) or nameof(Quality) or nameof(Pixels) or nameof(MaxSize) or nameof(Lossless) or nameof(NoUpscale))
        {
            if (Preset != "custom") Preset = "custom";
        }
        if (e.PropertyName is nameof(State) or nameof(Total) or nameof(Completed) or nameof(DryRun) or nameof(Lossless) or nameof(ResizeIndex) or nameof(EngineIndex) or nameof(Config) or nameof(PcAvailable) or nameof(Checking) or nameof(Message) or nameof(Preset) or nameof(Quality) or nameof(Pixels) or nameof(NoUpscale)) NotifyDerived();
    }
    private void NotifyDerived()
    {
        foreach (var name in new[] { nameof(IsRunning), nameof(IsLocked), nameof(IsUnknown), nameof(CanEdit), nameof(CanStart), nameof(CanStartTask), nameof(CanCancel), nameof(CanInspect), nameof(CanOpenOutput), nameof(HasMessage), nameof(HasFailures), nameof(HasTasks), nameof(IsIndeterminate), nameof(CanSetQuality), nameof(CanResize), nameof(NasEligible), nameof(Progress), nameof(ProgressText), nameof(StartText), nameof(StateTitle), nameof(AdvancedSummary), nameof(TaskSummary), nameof(EffectivePcConcurrency), nameof(JpegSelected), nameof(WebpSelected), nameof(ShareSelected) }) base.OnPropertyChanged(new PropertyChangedEventArgs(name));
    }
    private void NotifyTasks() { OnPropertyChanged(nameof(TaskSummary)); OnPropertyChanged(nameof(HasTasks)); }

    public async Task InitializeAsync()
    {
        try { Config = await store.LoadAsync(); }
        catch (Exception ex) { hasConfigurationError = true; Message = $"配置无法读取，原文件已保留。请在设置中导入或保存有效配置：{ex.Message}"; }
        await RecoverTasksAsync();
        await ProbeSelectedAsync();
    }
    private async Task RecoverTasksAsync()
    {
        if (Directory.Exists(TasksPath))
        {
            foreach (var file in Directory.EnumerateFiles(TasksPath, "*.json"))
            {
                try
                {
                    var request = Wire.Decode<WorkerRequest>(await File.ReadAllTextAsync(file));
                    AddRecovered(request, "上次任务尚未确认完成，请重新检查。");
                }
                catch { }
            }
        }
        if (File.Exists(PendingPath))
        {
            try
            {
                var request = Wire.Decode<WorkerRequest>(await File.ReadAllTextAsync(PendingPath));
                if (!taskIndex.ContainsKey(request.JobId))
                {
                    sources = request.Sources; EngineIndex = request.Engine == "nas" ? 1 : 0;
                }
            }
            catch (Exception ex) { Message = $"任务记录无法读取，请保留记录并检查：{ex.Message}"; }
        }
        NotifyTasks();
    }
    private CompressionTaskViewModel AddRecovered(WorkerRequest request, string message)
    {
        var task = new CompressionTaskViewModel(request, request.Engine == "nas" ? 1 : 0)
        {
            State = "unknown",
            Message = message,
            ResultSummary = "状态待确认"
        };
        taskIndex[task.JobId] = task;
        Tasks.Add(task);
        SetPrimary(task, writeMarker: false);
        return task;
    }
    public void SelectPreset(string name)
    {
        var options = CompressionOptions.ForPreset(name, new());
        initializing = true;
        FormatIndex = options.Format == "jpeg" ? 1 : 2; Quality = options.Quality; ResizeIndex = 1; Pixels = options.Pixels;
        Lossless = false; MaxSize = 0; NoUpscale = true; Preset = name;
        initializing = false; NotifyDerived();
    }
    public async Task SetSourcesAsync(IEnumerable<string> selection)
    {
        var paths = selection.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        if (paths.Length > 1 && (paths.Any(Directory.Exists) || paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1))
        { Message = "请选择一个文件夹，或同一目录内的多张图片。"; return; }
        sources = paths;
        SourceTitle = paths.Length > 1 ? $"已选择 {paths.Length} 张图片" : Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar));
        SourcePath = string.Join("; ", paths); SourceSummary = "开始前将统计当前处理范围";
        OutputPath = "源目录_compressed（已存在时自动编号）";
        State = "ready"; Message = ""; ResultSummary = "原始图片保持不变";
        EngineIndex = NasEligible ? 1 : 0;
        NotifyDerived();
        await ProbeSelectedAsync();
    }
    public void ClearSources()
    {
        sources = []; State = "empty"; SourceTitle = "将图片或文件夹拖到这里"; SourcePath = "支持 JPG、PNG、WebP、GIF";
        SourceSummary = "添加来源后开始"; OutputPath = "选择来源后自动生成"; Message = ""; NotifyDerived();
    }
    private WorkerRequest Request(string operation, string? engine = null) => new()
    {
        Operation = operation, Engine = engine ?? (EngineIndex == 1 ? "nas" : "pc"), Sources = sources,
        Config = Config, EnginePath = enginePath, StateDirectory = store.DirectoryPath
    };
    public async Task ProbeSelectedAsync()
    {
        var generation = ++probeGeneration;
        var request = Request("probe");
        EngineStatus = "正在检测…";
        try
        {
            if (request.Engine == "nas" && Config.ValidateConnection() is { } validation) { EngineStatus = validation; return; }
            WorkerEvent? result = null;
            await worker.ExecuteAsync(request, e => result = e);
            if (generation != probeGeneration) return;
            if (request.Engine == "pc")
            {
                PcAvailable = result?.PcAvailable == true;
                EngineStatus = PcAvailable ? "本机引擎就绪" : "本机引擎缺失，请重新安装应用";
            }
            else EngineStatus = result?.NasAvailable == true ? "NAS 引擎就绪" : result?.Connected == true ? "SSH 已连接；NAS 引擎或必要工具缺失，请查看设置说明" : "NAS 未连接，请检查设置";
        }
        catch (Exception ex) { if (generation == probeGeneration) EngineStatus = ex.Message; }
    }
    public async Task SetThemeAsync(string theme)
    {
        if (theme is not ("system" or "light" or "dark")) return;
        Config = Config with { Theme = theme };
        if (hasConfigurationError) { Message = "主题已临时生效。请先在设置中修复配置，避免覆盖无法读取的原文件。"; return; }
        try { await store.SaveAsync(Config); } catch (Exception ex) { Message = $"主题无法保存：{ex.Message}"; }
    }
    public async Task SaveConnectionAsync(AppConfig draft)
    {
        if (!string.IsNullOrWhiteSpace(draft.Server) && draft.ValidateConnection() is { } validation) throw new ArgumentException(validation);
        var updated = draft.Normalize() with { Theme = Config.Theme };
        if (hasConfigurationError && File.Exists(store.FilePath)) File.Copy(store.FilePath, store.FilePath + $".invalid-{DateTime.Now:yyyyMMddHHmmss}", false);
        await store.SaveAsync(updated); Config = updated; hasConfigurationError = false;
        await ProbeSelectedAsync();
    }
    public async Task<WorkerEvent> TestConnectionAsync(AppConfig draft)
    {
        if (draft.ValidateConnection() is { } error) throw new ArgumentException(error);
        WorkerEvent? result = null;
        await worker.ExecuteAsync(Request("probe", "nas") with { Config = draft }, e => result = e);
        return result ?? throw new IOException("没有收到连接测试结果。");
    }
    private CompressionOptions BuildOptions()
    {
        static int Integer(double value, string field)
        {
            if (!double.IsFinite(value) || value != Math.Truncate(value) || value > int.MaxValue || value < int.MinValue) throw new ArgumentException($"{field}需为有效整数。");
            return (int)value;
        }
        if (!Lossless && (!double.IsFinite(MaxSize) || MaxSize != Math.Truncate(MaxSize) || MaxSize < 0 || MaxSize > 9_007_199_254_740_991)) throw new ArgumentException("目标体积需为有效的非负整数字节。");
        var formats = new[] { "keep", "jpeg", "webp", "png" }; var resizes = new[] { "none", "long", "short", "width", "height" };
        if (FormatIndex < 0 || FormatIndex >= formats.Length || ResizeIndex < 0 || ResizeIndex >= resizes.Length) throw new ArgumentException("请选择格式和缩放方式。");
        return new()
        {
            Preset = Preset, Format = formats[FormatIndex], Resize = resizes[ResizeIndex], Quality = Lossless ? 90 : Integer(Quality, "质量"),
            Pixels = ResizeIndex == 0 ? 0 : Integer(Pixels, "尺寸"), MaxSize = Lossless ? 0 : (long)MaxSize,
            Threads = Integer(Threads, "线程数"), NoUpscale = ResizeIndex != 0 && NoUpscale, Lossless = Lossless, Recurse = Recurse, DryRun = DryRun
        };
    }
    public async Task StartAsync()
    {
        if (!CanStartTask) return;
        try
        {
            var options = BuildOptions();
            if (options.Validate() is { } validation) throw new ArgumentException(validation);
            if (EngineIndex == 1 && Config.ValidateConnection() is { } connectionError) throw new ArgumentException(connectionError);
            var request = Request("run") with { Options = options };
            if (TryFocusDuplicate(request.Sources)) return;
            var task = new CompressionTaskViewModel(request, EngineIndex);
            taskIndex[task.JobId] = task;
            Tasks.Insert(0, task);
            SetPrimary(task, writeMarker: true);
            await PersistTaskAsync(task);
            Message = ""; NotifyDerived(); NotifyTasks();
            await RunTaskAsync(task);
        }
        catch (Exception ex) { Message = ex.Message; }
    }
    private bool TryFocusDuplicate(string[] requested)
    {
        var existing = Tasks.FirstOrDefault(t => !t.TerminalConfirmed
            && t.Request.Sources.Length == requested.Length
            && t.Request.Sources.Select(Path.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(requested.Select(Path.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)));
        if (existing is null) return false;
        Message = $"该来源已在任务列表中（{existing.StateTitle}），未重复加入。";
        return true;
    }
    private async Task PersistTaskAsync(CompressionTaskViewModel task)
    {
        Directory.CreateDirectory(TasksPath);
        var file = Path.Combine(TasksPath, task.JobId + ".json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, Wire.Encode(task.Request));
        File.Move(temporary, file, true);
    }
    private async Task RemoveTaskRecordAsync(CompressionTaskViewModel task)
    {
        try
        {
            var file = Path.Combine(TasksPath, task.JobId + ".json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { }
        await Task.CompletedTask;
    }
    private void SetPrimary(CompressionTaskViewModel task, bool writeMarker)
    {
        primaryRequest = task.Request;
        State = task.State; Total = task.Total; Completed = task.Completed; CurrentFile = task.CurrentFile;
        Message = task.Message; ResultSummary = task.ResultSummary; OutputPath = task.OutputPath; OpenOutputPath = task.OpenOutputPath;
        if (writeMarker)
        {
            try
            {
                Directory.CreateDirectory(store.DirectoryPath);
                File.WriteAllText(PendingPath, Wire.Encode(task.Request));
            }
            catch { }
        }
    }
    private void Mirror(CompressionTaskViewModel task)
    {
        if (primaryRequest is null || task.JobId != primaryRequest.JobId) return;
        State = task.State; Total = task.Total; Completed = task.Completed; CurrentFile = task.CurrentFile;
        Message = task.Message; ResultSummary = task.ResultSummary; OutputPath = task.OutputPath; OpenOutputPath = task.OpenOutputPath;
        if (Failures.Count != task.Failures.Count)
        {
            Failures.Clear();
            foreach (var failure in task.Failures) Failures.Add(failure);
        }
        if (task.TerminalConfirmed)
        {
            try { if (File.Exists(PendingPath)) File.Delete(PendingPath); } catch { }
        }
        NotifyDerived(); NotifyTasks();
    }
    private async Task WaitForSlotAsync(CompressionTaskViewModel task)
    {
        while (true)
        {
            var runningPc = taskIndex.Values.Count(t => t.EngineIndex == 0 && t.IsRunning);
            var runningNas = taskIndex.Values.Count(t => t.EngineIndex == 1 && t.IsRunning);
            if (task.EngineIndex == 1 ? runningNas == 0 : runningPc < EffectivePcConcurrency) return;
            await Task.Delay(150);
        }
    }
    private async Task RunTaskAsync(CompressionTaskViewModel task)
    {
        try
        {
            await WaitForSlotAsync(task);
            var cores = Math.Max(1, Environment.ProcessorCount);
            var active = taskIndex.Values.Count(t => t.IsRunning) + 1;
            var threads = Math.Max(1, cores / Math.Min(active, cores));
            task.Request = task.Request with { Options = task.Request.Options with { Threads = threads } };
            if (primaryRequest is not null && primaryRequest.JobId == task.JobId) primaryRequest = task.Request;
            task.State = "preparing";
            NotifyTasks();
            await worker.ExecuteAsync(task.Request with { Operation = "run" },
                e => dispatch(() =>
                {
                    task.Apply(e); Mirror(task); NotifyTasks();
                    // 收尾必须在终态事件真正应用之后执行：await 返回时事件可能还没派发。
                    if (task.TerminalConfirmed) _ = FinalizeTaskAsync(task);
                }));
        }
        catch (Exception ex)
        {
            dispatch(() =>
            {
                if (!task.TerminalConfirmed) { task.State = "unknown"; task.Message = ex.Message; }
                Mirror(task); NotifyTasks();
            });
        }
        if (task.TerminalConfirmed)
        {
            await FinalizeTaskAsync(task);
        }
    }
    private async Task FinalizeTaskAsync(CompressionTaskViewModel task)
    {
        if (task.Finalized) return;
        task.Finalized = true;
        await RemoveTaskRecordAsync(task);
        if (task.State is "succeeded" or "dryRun" or "emptyResult")
        {
            dispatch(() =>
            {
                Tasks.Remove(task);
                taskIndex.Remove(task.JobId);
                NotifyTasks();
            });
        }
    }
    private CompressionTaskViewModel? PrimaryTask() =>
        primaryRequest is null ? null : (taskIndex.TryGetValue(primaryRequest.JobId, out var task) ? task : null);
    public async Task CancelTaskAsync(CompressionTaskViewModel task)
    {
        if (task.IsQueued)
        {
            Tasks.Remove(task); taskIndex.Remove(task.JobId);
            await RemoveTaskRecordAsync(task); NotifyTasks(); return;
        }
        if (!task.CanCancel) return;
        task.State = "cancelling"; NotifyTasks();
        try { await worker.ExecuteAsync(task.Request with { Operation = "cancel" }, e => dispatch(() => { task.Apply(e); Mirror(task); NotifyTasks(); if (task.TerminalConfirmed) _ = FinalizeTaskAsync(task); })); }
        catch (Exception ex) { dispatch(() => { if (!task.TerminalConfirmed) { task.State = "unknown"; task.Message = ex.Message; } Mirror(task); NotifyTasks(); }); }
        if (task.TerminalConfirmed) await FinalizeTaskAsync(task);
    }
    public async Task InspectTaskAsync(CompressionTaskViewModel task)
    {
        if (!task.CanInspect) return;
        task.Checking = true; NotifyTasks();
        try { await worker.ExecuteAsync(task.Request with { Operation = "inspect" }, e => dispatch(() => { task.Apply(e); Mirror(task); NotifyTasks(); if (task.TerminalConfirmed) _ = FinalizeTaskAsync(task); })); }
        catch (Exception ex) { dispatch(() => task.Message = ex.Message); }
        finally { task.Checking = false; NotifyTasks(); }
        if (task.TerminalConfirmed) await FinalizeTaskAsync(task);
    }
    public Task CancelAsync() => PrimaryTask() is { } task ? CancelTaskAsync(task) : Task.CompletedTask;
    public Task InspectAsync() => PrimaryTask() is { } task ? InspectTaskAsync(task) : Task.CompletedTask;
    public void Apply(WorkerEvent e)
    {
        if (taskIndex.TryGetValue(e.JobId, out var task))
        {
            task.Apply(e); Mirror(task); NotifyTasks();
        }
    }
}
