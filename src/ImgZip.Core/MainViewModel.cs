using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImgZip.Core;

public partial class MainViewModel : ObservableObject
{
    private readonly IWorkerClient worker;
    private readonly ConfigStore store;
    private readonly string enginePath;
    private WorkerRequest? primaryRequest;
    private string[] sources = [];
    private bool initializing = true;
    private bool hasConfigurationError;
    private int probeGeneration;

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
    [ObservableProperty] private bool copySkippedFiles;
    [ObservableProperty] private bool folderSource;
    [ObservableProperty] private bool dryRun;
    [ObservableProperty] private bool pcAvailable;
    /// <summary>NAS 探测结果：1a 由协调者持有，窗口同步一份可绑定的副本。</summary>
    [ObservableProperty] private int nasCores;
    [ObservableProperty] private long nasMemoryMb;
    [ObservableProperty] private bool advancedExpanded;
    [ObservableProperty] private bool checking;
    [ObservableProperty] private int total;
    [ObservableProperty] private int completed;
    [ObservableProperty] private string preset = "archive-jpeg";
    /// <summary>
    /// 本窗口呈现的任务。阶段 2a 只新增，不取代 <see cref="Tasks"/>：
    /// 任务被路由到本窗口时（<c>SetPrimary</c>）写入，窗口关闭或任务被移除后仍保留，供结果态展示。
    /// </summary>
    [ObservableProperty] private CompressionTaskViewModel? currentTask;
    public ObservableCollection<string> Failures { get; } = [];

    /// <summary>应用级任务协调者；未显式传入时自建一个，行为与拆分前的单窗口一致。</summary>
    public TaskCoordinator Coordinator { get; }
    public TaskCoordinator.Attachment Attachment { get; }

    public MainViewModel(IWorkerClient worker, ConfigStore store, string enginePath, Action<Action>? dispatch = null, ITaskStore? taskStore = null)
        : this(worker, store, enginePath, dispatch, taskStore, null) { }

    /// <summary>协调者参数只能追加在末尾，保持既有位置参数调用不变。</summary>
    public MainViewModel(IWorkerClient worker, ConfigStore store, string enginePath, Action<Action>? dispatch, ITaskStore? taskStore, TaskCoordinator? coordinator)
    {
        this.worker = worker; this.store = store; this.enginePath = enginePath;
        // C2：dispatch 与 taskStore 必须原样透传给协调者，漏任一都会破坏既有测试的语义。
        Coordinator = coordinator ?? new TaskCoordinator(worker, taskStore ?? new TaskStore(store.DirectoryPath), dispatch);
        // 回调按窗口挂接：协调者只通知持有该任务的窗口，Detach 后不再收到事件。
        Attachment = Coordinator.Attach(SetPrimary, Mirror, NotifyTasks, NotifyDerived, notice => Message = notice, () => OnConfigChanged());
        initializing = false;
    }
    private void OnConfigChanged()
    {
        // 协调者的配置是权威副本；窗口跟随，但不再各自落盘覆盖。
        Config = Coordinator.Config;
        CopySkippedFiles = Config.CopySkippedFiles;
    }

    // ---- 草稿（新建任务）状态 ----
    public bool IsRunning => State is "preparing" or "running" or "cancelling";
    public bool IsLocked => IsRunning || State == "unknown";
    public bool IsUnknown => State == "unknown";
    public bool CanEdit => Coordinator.TasksReady;
    public bool HasUnfinishedTasks => Coordinator.HasUnfinishedTasks;
    /// <summary>本窗口是否还有一个未结束的任务。窗口的关闭守卫与"运行中"判断都基于它。</summary>
    public bool CurrentTaskUnfinished => CurrentTask is { TerminalConfirmed: false };
    public bool CanStart => CanEdit && sources.Length > 0 && (EngineIndex == 1 ? NasEligible : PcAvailable || DryRun);
    /// <summary>草稿是否可启动新任务：多任务下不再受“运行中”限制。</summary>
    public bool CanStartTask => CanEdit && sources.Length > 0 && (EngineIndex == 1 ? NasEligible : PcAvailable || DryRun);
    public bool CanCancel => State is "preparing" or "running";
    public bool CanInspect => State == "unknown" && !Checking;
    public bool CanOpenOutput => !IsLocked && State is "succeeded" or "partial" && !string.IsNullOrEmpty(OpenOutputPath);
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasFailures => Failures.Count > 0;
    /// <summary>应用内是否有任务（池级）。阶段 2 起窗口不再持有任务集合，判据上移到协调者。</summary>
    public bool HasTasks => Coordinator.Tasks.Count > 0;
    public bool IsIndeterminate => State == "preparing" || (IsRunning && Total == 0);
    public bool CanSetQuality => !Lossless;
    public bool CanResize => ResizeIndex != 0;
    public bool CanCopySkippedFiles => CanEdit && FolderSource;
    public bool NasEligible => !string.IsNullOrWhiteSpace(Config.Server) && sources.Length > 0 && sources.All(p => p.StartsWith(@"\\") || LooksLikeMappedDrive(p));
    public bool JpegSelected => Preset == "archive-jpeg";
    public bool WebpSelected => Preset == "archive-webp";
    public bool ShareSelected => Preset == "webp-share";
    public double Progress => Total == 0 ? 0 : 100d * Completed / Total;
    public string ProgressText => $"{Completed} / {Total}";
    public string StartText => DryRun ? "开始预演" : "开始压缩";
    public int EffectivePcConcurrency => Coordinator.EffectivePcConcurrency;
    public int EffectiveNasConcurrency => Coordinator.EffectiveNasConcurrency;
    public string NasConcurrencyText
    {
        get
        {
            var hardware = NasCores > 0 ? Math.Min(NasCores, 4) : 4;
            var detected = NasCores > 0 ? $"NAS {NasCores} 核" : "NAS 核数待探测";
            return $"NAS 并发 {EffectiveNasConcurrency}（上限 {hardware}，{detected}）";
        }
    }
    /// <summary>
    /// 本窗口任务的一句话摘要。阶段 2 起窗口只陈述自己那一个任务，
    /// 池级统计由 <see cref="TaskCoordinator.Tasks"/> 承担。
    /// </summary>
    public string TaskSummary
    {
        get
        {
            if (CurrentTask is not { } task) return "暂无任务";
            var concurrency = task.EngineIndex == 1
                ? $"NAS 并发 {EffectiveNasConcurrency}"
                : $"PC 并发 {EffectivePcConcurrency}";
            var progress = task.ProgressText is { Length: > 0 } text ? $" · {text}" : "";
            return $"{task.StateTitle} · {task.EngineName} · {concurrency}{progress}";
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
        if (e.PropertyName is nameof(State) or nameof(Total) or nameof(Completed) or nameof(DryRun) or nameof(Lossless) or nameof(ResizeIndex) or nameof(EngineIndex) or nameof(Config) or nameof(PcAvailable) or nameof(NasCores) or nameof(NasMemoryMb) or nameof(Checking) or nameof(Message) or nameof(Preset) or nameof(Quality) or nameof(Pixels) or nameof(NoUpscale) or nameof(CopySkippedFiles) or nameof(FolderSource) or nameof(CurrentTask)) NotifyDerived();
    }
    private void NotifyDerived()
    {
        foreach (var name in new[] { nameof(IsRunning), nameof(IsLocked), nameof(IsUnknown), nameof(CanEdit), nameof(CanStart), nameof(CanStartTask), nameof(CanCancel), nameof(CanInspect), nameof(CanOpenOutput), nameof(HasMessage), nameof(HasFailures), nameof(HasTasks), nameof(HasUnfinishedTasks), nameof(CurrentTaskUnfinished), nameof(IsIndeterminate), nameof(CanSetQuality), nameof(CanResize), nameof(CanCopySkippedFiles), nameof(NasEligible), nameof(Progress), nameof(ProgressText), nameof(StartText), nameof(StateTitle), nameof(AdvancedSummary), nameof(TaskSummary), nameof(EffectivePcConcurrency), nameof(EffectiveNasConcurrency), nameof(NasConcurrencyText), nameof(JpegSelected), nameof(WebpSelected), nameof(ShareSelected) }) base.OnPropertyChanged(new PropertyChangedEventArgs(name));
    }
    private void NotifyTasks() { OnPropertyChanged(nameof(TaskSummary)); OnPropertyChanged(nameof(HasTasks)); OnPropertyChanged(nameof(HasUnfinishedTasks)); OnPropertyChanged(nameof(CurrentTaskUnfinished)); }

    public async Task InitializeAsync()
    {
        try { Config = await store.LoadAsync(); CopySkippedFiles = Config.CopySkippedFiles; }
        catch (Exception ex) { hasConfigurationError = true; Message = $"配置无法读取，原文件已保留。请在设置中导入或保存有效配置：{ex.Message}"; }
        // 配置是全局的：第一个初始化的窗口把磁盘内容交给协调者，后续窗口沿用同一份。
        Coordinator.AdoptConfig(Config);
        await Coordinator.EnsureRecoveredAsync();
        Coordinator.MarkReady(); NotifyDerived();
        await ProbeSelectedAsync();
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
        FolderSource = paths.Length == 1 && Directory.Exists(paths[0]);
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
        sources = []; FolderSource = false; State = "empty"; SourceTitle = "将图片或文件夹拖到这里"; SourcePath = "支持 JPG、PNG、WebP、GIF";
        SourceSummary = "添加来源后开始"; OutputPath = "选择来源后自动生成"; Message = ""; NotifyDerived();
    }
    private WorkerRequest Request(string operation, string? engine = null) => new()
    {
        Operation = operation, Engine = engine ?? (EngineIndex == 1 ? "nas" : "pc"), Sources = sources.ToArray(),
        Config = Config with { NasHosts = Config.NasHosts.ToArray() }, EnginePath = enginePath, StateDirectory = store.DirectoryPath
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
            else
            {
                var cores = result?.NasCores > 0 ? result.NasCores : 0;
                var memory = result?.NasMemoryMb > 0 ? result.NasMemoryMb : 0;
                Coordinator.SetNasProbeResult(cores, memory);
                NasCores = cores; NasMemoryMb = memory;
                var memoryText = memory > 0 ? $" · 可用内存 {Display.Bytes(memory * 1024 * 1024)}" : "";
                EngineStatus = result?.NasAvailable == true
                    ? $"NAS 引擎就绪 · {NasConcurrencyText}{memoryText}"
                    : result?.Connected == true
                        ? "SSH 已连接；NAS 引擎或必要工具缺失，请查看设置说明"
                        : string.IsNullOrWhiteSpace(result?.Message) ? "NAS 未连接，请检查设置" : result.Message;
            }
        }
        catch (Exception ex) { if (generation == probeGeneration) EngineStatus = ex.Message; }
    }
    public async Task SetThemeAsync(string theme)
    {
        if (theme is not ("system" or "light" or "dark")) return;
        if (hasConfigurationError) { Config = Config with { Theme = theme }; Message = "主题已临时生效。请先在设置中修复配置，避免覆盖无法读取的原文件。"; return; }
        try
        {
            // 读-改-写：避免另一个窗口在此期间保存的连接设置被本窗口的整份配置覆盖。
            await Coordinator.UpdateConfigAsync(config => config with { Theme = theme }, updated => store.SaveAsync(updated));
            Config = Coordinator.Config;
        }
        catch (Exception ex) { Config = Config with { Theme = theme }; Message = $"主题无法保存：{ex.Message}"; }
    }
    public async Task SetCopySkippedFilesAsync(bool value)
    {
        CopySkippedFiles = value;
        if (hasConfigurationError) { Config = Config with { CopySkippedFiles = value }; Message = "选项已临时生效。请先在设置中修复配置，避免覆盖无法读取的原文件。"; return; }
        try
        {
            await Coordinator.UpdateConfigAsync(config => config with { CopySkippedFiles = value }, updated => store.SaveAsync(updated));
            Config = Coordinator.Config;
        }
        catch (Exception ex) { Config = Config with { CopySkippedFiles = value }; Message = $"复制其他文件偏好无法保存：{ex.Message}"; }
    }
    public async Task SaveConnectionAsync(AppConfig draft)
    {
        if (!string.IsNullOrWhiteSpace(draft.Server) && draft.ValidateConnection() is { } validation) throw new ArgumentException(validation);
        if (hasConfigurationError && File.Exists(store.FilePath)) File.Copy(store.FilePath, store.FilePath + $".invalid-{DateTime.Now:yyyyMMddHHmmss}", false);
        try
        {
            await Coordinator.UpdateConfigAsync(
                config => draft.Normalize() with { Theme = config.Theme, CopySkippedFiles = config.CopySkippedFiles },
                updated => store.SaveAsync(updated));
            Config = Coordinator.Config; hasConfigurationError = false;
        }
        catch
        {
            // 保存失败时窗口仍显示草稿，方便重试；不写入协调者。
            Config = draft.Normalize() with { Theme = Config.Theme, CopySkippedFiles = Config.CopySkippedFiles };
            throw;
        }
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
            Threads = Integer(Threads, "线程数"), NoUpscale = ResizeIndex != 0 && NoUpscale, Lossless = Lossless, Recurse = Recurse,
            CopySkippedFiles = CopySkippedFiles && FolderSource, DryRun = DryRun
        };
    }
}
