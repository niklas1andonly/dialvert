using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Dialvert.App.Interop;

/// <summary>
/// Keeps one running instance. A second launch (e.g. from "Send to") hands its file
/// arguments to the first instance over a named pipe and exits.
/// </summary>
sealed class SingleInstance : IDisposable
{
    readonly string _pipeName = $"Dialvert-{Environment.UserName}";
    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\Dialvert-{Environment.UserName}", out var created);
        IsFirst = created;
    }

    public bool SendToFirst(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(string.Join("\n", args));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { return false; }
    }

    public void Listen(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(_cts.Token);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* client vanished; keep listening */ }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
