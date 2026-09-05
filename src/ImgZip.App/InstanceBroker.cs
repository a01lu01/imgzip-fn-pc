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
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(Wire.Encode(paths));
    }
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
