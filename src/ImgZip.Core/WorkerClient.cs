using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ImgZip.Core;

public interface IWorkerClient
{
    Task ExecuteAsync(WorkerRequest request, Action<WorkerEvent> onEvent);
}

public sealed class WorkerClient(string shellPath, string scriptPath) : IWorkerClient, IWorkerTracker
{
    // 每个在跑的 jobId 对应一个执行层进程句柄。关闭窗口时需要它来终止进程树。
    private readonly ConcurrentDictionary<string, Process> running = new(StringComparer.Ordinal);

    public event Action<string, int, DateTimeOffset>? ProcessStarted;

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
        // 只有真正的 run/inspect/cancel 才登记；probe 之类短命进程不需要被终止。
        var tracked = request.Operation is "run" or "inspect" or "cancel";
        if (tracked)
        {
            running[request.JobId] = process;
            // 启动时刻必须在进程启动后立刻读取：查 PID 是否复用时靠它区分同一个 ID 的不同进程。
            try { ProcessStarted?.Invoke(request.JobId, process.Id, new DateTimeOffset(process.StartTime)); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        try
        {
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
        finally
        {
            if (tracked) running.TryRemove(request.JobId, out _);
        }
    }

    /// <summary>
    /// 终止该任务的执行层进程树并等待确认。子进程（caesiumclt）会一并被带走。
    /// 先查本进程登记的句柄；重启后接管的任务没有句柄，退回到记录里的 PID。
    /// 返回 false 表示无法确认已停止：调用方必须保留记录并标注未确认。
    /// </summary>
    public async Task<bool> TerminateAsync(string jobId, int processId, DateTimeOffset startedAt)
    {
        if (running.TryRemove(jobId, out var tracked))
        {
            try
            {
                if (!tracked.HasExited) tracked.Kill(entireProcessTree: true);
                if (!await WaitForExitAsync(tracked, TimeSpan.FromSeconds(5))) return false;
                return tracked.HasExited;
            }
            catch (InvalidOperationException) { return true; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (NotSupportedException) { return false; }
            finally { try { tracked.Dispose(); } catch { } }
        }
        // 没有句柄：只有记录了 PID 才能接管终止，且必须校验启动时刻以排除 PID 复用。
        if (processId <= 0) return true;
        Process? adopted = null;
        try
        {
            adopted = Process.GetProcessById(processId);
            if (adopted.HasExited) return true;
            if (startedAt != default)
            {
                var actual = new DateTimeOffset(adopted.StartTime);
                if (actual.Offset != startedAt.Offset) actual = actual.ToOffset(startedAt.Offset);
                // 启动时刻对不上说明这个 PID 已经是别的进程，不能杀。
                if ((actual - startedAt).Duration() > TimeSpan.FromSeconds(2)) return true;
            }
            adopted.Kill(entireProcessTree: true);
            return await WaitForExitAsync(adopted, TimeSpan.FromSeconds(5)) && adopted.HasExited;
        }
        catch (ArgumentException) { return true; }          // PID 不存在，本来就没在跑
        catch (InvalidOperationException) { return true; }  // 已退出
        catch (System.ComponentModel.Win32Exception) { return false; } // 拒绝访问，无法确认
        catch (NotSupportedException) { return false; }
        finally { try { adopted?.Dispose(); } catch { } }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
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
