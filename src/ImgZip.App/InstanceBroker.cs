using System.IO.Pipes;
using System.Security.Principal;
using ImgZip.Core;

namespace ImgZip.App;
internal sealed class InstanceBroker : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private readonly string name;
    public bool IsFirst { get; }
    public InstanceBroker()
    {
        name = "ImgZip-" + WindowsIdentity.GetCurrent().User!.Value;
        mutex = new Mutex(true, @"Local\" + name, out var created);
        IsFirst = created;
    }
    public async Task SendAsync(string[] paths)
    {
        // 我们此刻持有前台权（由资源管理器启动）。把它让给接收方，否则它建完窗口调
        // SetForegroundWindow 会被系统拒绝，只闪一下任务栏。
        // 用 ASFW_ANY 而不是具体 PID：发送方拿不到接收方的 PID，而查询它的代价（写/读 PID 文件）
        // 换来的是同样的效果，不值得多一条失效路径。授权只在本次转发后的一小段时间内有意义。
        AllowSetForegroundWindow(AsfwAny);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(Wire.Encode(paths));
    }
    private const int AsfwAny = -1;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
    public async Task ListenAsync(Action<string[]> receive)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(pipe);
                var line = await reader.ReadLineAsync(stop.Token);
                if (line is { Length: < 65536 }) receive(Wire.Decode<string[]>(line));
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (System.Text.Json.JsonException) { }
        }
    }
    public void Dispose()
    {
        stop.Cancel();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose(); stop.Dispose();
    }
}
