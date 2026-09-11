using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImgZip.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new JsonException("JSON 不能为空。");
}

public sealed record AppConfig
{
    public string Server { get; init; } = "";
    public string User { get; init; } = "";
    public string[] NasHosts { get; init; } = [];
    public string NasIp { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public int Port { get; init; } = 22;
    public string Theme { get; init; } = "system";
    /// <summary>PC 并行任务数；0 = 自动（等于 CPU 核数）。</summary>
    public int PcConcurrency { get; init; }
    public AppConfig Normalize() => this with
    {
        Server = (Server ?? "").Trim(), User = (User ?? "").Trim(), NasIp = (NasIp ?? "").Trim(),
        KeyPath = (KeyPath ?? "").Trim(), NasHosts = NasHosts ?? [],
        Theme = Theme is "light" or "dark" ? Theme : "system",
        PcConcurrency = PcConcurrency is 0 or (> 0 and <= 64) ? PcConcurrency : 0
    };
    public string? ValidateConnection()
    {
        if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(User) || string.IsNullOrWhiteSpace(KeyPath)) return "请填写服务器、SSH 用户和私钥路径。";
        if (Port is < 1 or > 65535) return "SSH 端口需为 1–65535。";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Server, @"^[a-zA-Z0-9][a-zA-Z0-9.:-]*$")) return "服务器地址只能包含主机名或 IP。";
        if (!System.Text.RegularExpressions.Regex.IsMatch(User, @"^[a-zA-Z0-9_][a-zA-Z0-9_.-]*$")) return "SSH 用户名包含不支持的字符。";
        return null;
    }
}

public sealed record CompressionOptions
{
    public string Preset { get; init; } = "archive-jpeg";
    public string Format { get; init; } = "jpeg";
    public int Quality { get; init; } = 90;
    public string Resize { get; init; } = "long";
    public int Pixels { get; init; } = 4000;
    public long MaxSize { get; init; }
    public int Threads { get; init; } = 4;
    public bool Lossless { get; init; }
    public bool NoUpscale { get; init; } = true;
    public bool Recurse { get; init; }
    public bool DryRun { get; init; }
    public static CompressionOptions ForPreset(string name, CompressionOptions previous) => name switch
    {
        "archive-jpeg" => previous with { Preset = name, Format = "jpeg", Quality = 90, Resize = "long", Pixels = 4000, Lossless = false, MaxSize = 0, NoUpscale = true },
        "archive-webp" => previous with { Preset = name, Format = "webp", Quality = 90, Resize = "long", Pixels = 4000, Lossless = false, MaxSize = 0, NoUpscale = true },
        "webp-share" => previous with { Preset = name, Format = "webp", Quality = 80, Resize = "long", Pixels = 2560, Lossless = false, MaxSize = 0, NoUpscale = true },
        _ => throw new ArgumentException("未知预设。", nameof(name))
    };
    public string? Validate()
    {
        if (Format is not ("keep" or "jpeg" or "webp" or "png")) return "输出格式无效。";
        if (Resize is not ("none" or "long" or "short" or "width" or "height")) return "缩放方式无效。";
        if (!Lossless && Quality is < 0 or > 100) return "质量需为 0–100。";
        if (Resize != "none" && Pixels < 1) return "缩放尺寸需为正整数。";
        if (!Lossless && MaxSize < 0) return "目标体积不能为负数。";
        if (Threads is < 1 or > 64) return "线程数需为 1–64。";
        return null;
    }
}

public sealed record WorkerRequest
{
    public int Version { get; init; } = 1;
    public string Operation { get; init; } = "run";
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    public string Engine { get; init; } = "pc";
    public string[] Sources { get; init; } = [];
    public CompressionOptions Options { get; init; } = new();
    public AppConfig Config { get; init; } = new();
    public string EnginePath { get; init; } = "";
    // Fixed to ssh in the application; injectable for non-network execution-layer tests.
    public string SshPath { get; init; } = "ssh";
    public string StateDirectory { get; init; } = "";
}

public sealed record WorkerEvent
{
    public int Version { get; init; } = 1;
    public string JobId { get; init; } = "";
    public string Type { get; init; } = "";
    public string State { get; init; } = "";
    public string Message { get; init; } = "";
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Index { get; init; } = -1;
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public long BeforeBytes { get; init; }
    public long AfterBytes { get; init; }
    public string Path { get; init; } = "";
    public string Output { get; init; } = "";
    public string OpenOutput { get; init; } = "";
    public bool PcAvailable { get; init; }
    public bool NasAvailable { get; init; }
    public bool Connected { get; init; }
    public bool DryRun { get; init; }
    public static WorkerEvent Parse(string json, string jobId)
    {
        var item = Wire.Decode<WorkerEvent>(json);
        if (item.Version != 1 || item.JobId != jobId) throw new InvalidDataException("执行层事件版本或任务标识不匹配。");
        if (item.Type is not ("phase" or "manifest" or "file" or "finished" or "probe" or "resolved" or "error" or "cancelRequested")) throw new InvalidDataException("执行层返回未知事件。");
        if (item.Completed < 0 || item.Total < 0 || item.Completed > item.Total || item.BeforeBytes < 0 || item.AfterBytes < 0) throw new InvalidDataException("执行层返回无效统计。");
        if (item.Succeeded < 0 || item.Failed < 0 || item.Skipped < 0 || (long)item.Succeeded + item.Failed > item.Completed) throw new InvalidDataException("执行层返回无效结果计数。");
        if (item.Type == "finished" && item.State is not ("succeeded" or "partial" or "failed" or "cancelled" or "unknown" or "dryRun" or "empty")) throw new InvalidDataException("执行层返回无效终态。");
        return item;
    }
}

public static class Display
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes} B"
    };
    public static string Summary(WorkerEvent result)
    {
        if (result.DryRun) return $"预演完成：{result.Total} 张图片，跳过 {result.Skipped} 项。未生成文件。";
        var counts = $"成功 {result.Succeeded} · 失败 {result.Failed} · 跳过 {result.Skipped}";
        if (result.BeforeBytes <= 0) return counts;
        var percent = (1 - result.AfterBytes / (double)result.BeforeBytes) * 100;
        return $"{counts}\n成功项：{Bytes(result.BeforeBytes)} → {Bytes(result.AfterBytes)}（{(percent >= 0 ? "节省" : "增加")} {Math.Abs(percent):N1}%）";
    }
}
