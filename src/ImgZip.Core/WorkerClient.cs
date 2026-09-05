using System.Diagnostics;
using System.Text;

namespace ImgZip.Core;

public interface IWorkerClient
{
    Task ExecuteAsync(WorkerRequest request, Action<WorkerEvent> onEvent);
}

public sealed class WorkerClient(string shellPath, string scriptPath) : IWorkerClient
{
    public async Task ExecuteAsync(WorkerRequest request, Action<WorkerEvent> onEvent)
    {
        var start = new ProcessStartInfo(shellPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 PowerShell 执行层。");
        var stderr = DrainDiagnosticsAsync(process.StandardError, request.StateDirectory, request.JobId);
        await process.StandardInput.WriteLineAsync(Wire.Encode(request));
        process.StandardInput.Close();
        var received = false;
        var terminal = false;
        WorkerEvent? finalEvent = null;
        Exception? protocolError = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var message = WorkerEvent.Parse(line.TrimStart('\uFEFF'), request.JobId);
                if (terminal) throw new InvalidDataException("执行层在终态之后继续发送事件。");
                received = true;
                terminal = message.Type == "finished";
                if (terminal) finalEvent = message;
                else if (protocolError is null) onEvent(message);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                // Drain the process to prevent deadlock. Never turn a broken stream into success.
                protocolError ??= ex;
            }
        }
        await process.WaitForExitAsync();
        await stderr;
        if (protocolError is not null) throw new InvalidDataException("执行层协议异常；请重新检查任务状态。", protocolError);
        if (!received || ((request.Operation is "run" or "inspect") && !terminal)) throw new IOException("执行层连接中断；任务状态尚未确认。");
        if (process.ExitCode != 0) throw new IOException($"执行层异常退出（{process.ExitCode}）。");
        // A final event is trustworthy only after the complete stream and exit are validated.
        if (finalEvent is not null) onEvent(finalEvent);
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader, string directory, string jobId)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(directory, "logs"));
                // Do not log the request, connection fields, or key material.
                await File.AppendAllTextAsync(Path.Combine(directory, "logs", jobId + ".log"), $"{DateTimeOffset.Now:O} {line}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
