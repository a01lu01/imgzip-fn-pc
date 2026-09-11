using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Contains("bash") && args.Contains("-s"))
{
    // A transport fixture: parse the actual safe prelude, never execute the received Bash.
    var script = await Console.In.ReadToEndAsync();
    var variables = Regex.Matches(script, "(?m)^([A-Z_0-9]+)=\\$\\(printf '%s' '([A-Za-z0-9+/=]*)' \\| base64 -d\\)$")
        .ToDictionary(m => m.Groups[1].Value, m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[2].Value)));
    var mode = variables["MODE"]; var id = variables["JOB_ID"];
    string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    if (mode == "probe") { Emit(new { version = 1, jobId = id, type = "probe", connected = true, nasAvailable = !args.Any(a => a.EndsWith("@missing")) }); return 0; }
    if (mode == "resolve") { Emit(new { version = 1, jobId = id, type = "resolved", path64 = Encode("/vol1/photo") }); return 0; }
    var remoteOutput = "/vol1/photo/trip_compressed";
    if (mode == "cancel") { Emit(new { version = 1, jobId = id, type = "finished", state = "unknown", message64 = Encode("Remote termination not confirmed") }); return 0; }
    if (mode == "launch")
    {
        Emit(new { version = 1, jobId = id, type = "manifest", state = "ready", total = 1, completed = 0, output64 = Encode(remoteOutput) });
        var path = Encoding.UTF8.GetString(Convert.FromBase64String(variables["SOURCES_B64"].Split('\n')[0]));
        if (path.Contains("disconnect")) { Console.Error.WriteLine("Transport lost"); return 255; }
        Emit(new { version = 1, jobId = id, type = "file", state = "succeeded", index = 0, total = 1, completed = 1, succeeded = 1, beforeBytes = 100, afterBytes = 50, path64 = Encode(path), output64 = Encode(remoteOutput) });
    }
    Emit(new { version = 1, jobId = id, type = "finished", state = "succeeded", total = 1, completed = 1, succeeded = 1, beforeBytes = 100, afterBytes = 50, output64 = Encode(remoteOutput) });
    return 0;
}

// A deterministic child process to test the real worker without image codecs or a NAS.
if (args.SequenceEqual(["--version"])) { Console.WriteLine("caesiumclt fixture"); return 0; }
if (args.Length < 3) return 10;
var outputIndex = Array.IndexOf(args, "-o");
if (outputIndex < 0) return 11;
var output = args[outputIndex + 1];
if (!args.Contains("-e") || !args.Contains("--keep-dates") || !args.Contains("--threads")) return 13;
if (args.Contains("--lossless") && (args.Contains("-q") || args.Contains("--max-size"))) return 14;
var formatIndex = Array.IndexOf(args, "--format");
var format = formatIndex >= 0 ? args[formatIndex + 1] : null;
var verboseIndex = Array.IndexOf(args, "--verbose");
var verbose = verboseIndex >= 0 && verboseIndex + 1 < args.Length && int.TryParse(args[verboseIndex + 1], out var parsedVerbose) ? parsedVerbose : 1;

// 组批调用：收集全部输入文件（跳过选项及其参数），逐个产出与真实引擎一致的输出。
var valueOptions = new HashSet<string>(StringComparer.Ordinal)
{
    "-o", "--threads", "-q", "--format", "--max-size", "--long-edge", "--short-edge", "--width", "--height", "--verbose",
    "--jpeg-chroma-subsampling", "--png-opt-level"
};
var inputs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    var argument = args[i];
    if (valueOptions.Contains(argument)) { i++; continue; }
    if (argument.StartsWith('-')) continue;
    inputs.Add(argument);
}
if (inputs.Count == 0) { Console.Error.WriteLine("Source argument was corrupted."); return 12; }

Directory.CreateDirectory(output);
var success = 0; var errors = 0;
foreach (var source in inputs)
{
    var name = Path.GetFileName(source);
    if (!File.Exists(source)) { Console.Error.WriteLine($"Missing source: {source}"); errors++; continue; }
    if (name.StartsWith("slow", StringComparison.Ordinal)) await Task.Delay(TimeSpan.FromSeconds(20));
    if (name.StartsWith("fail", StringComparison.Ordinal)) { Console.Error.WriteLine($"Decoder failure fixture: {source}"); errors++; continue; }
    if (name.StartsWith("empty", StringComparison.Ordinal)) { Console.Error.WriteLine($"Empty output fixture: {source}"); errors++; continue; }
    var extension = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
    if (format is not null)
    {
        if ((extension == "jpg" ? "jpeg" : extension) == format)
        {
            Console.Error.WriteLine($"Same-format conversion is invalid: {source}");
            if (verbose >= 3) Console.WriteLine($"[Error] {source} -> {Path.Combine(output, name)}");
            errors++; continue;
        }
        extension = format == "jpeg" ? "jpg" : format;
    }
    var target = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + "." + extension);
    var bytes = await File.ReadAllBytesAsync(source);
    await File.WriteAllBytesAsync(target, bytes[..Math.Max(1, bytes.Length / 2)]);
    success++;
    if (verbose >= 3) Console.WriteLine($"[Success] {source} -> {target}");
}
Console.WriteLine($"Compressed {inputs.Count} files ({success} success, 0 skipped, {errors} errors)");
return 0;
