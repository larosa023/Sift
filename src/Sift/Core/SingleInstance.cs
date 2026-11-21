using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Sift.Core;

/// <summary>
/// Keeps one Sift window per user session. A second launch hands its request (show, scan a folder, quit)
/// to the running copy over a named pipe and exits. The pipe is current-user only.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Session = Process.GetCurrentProcess().SessionId.ToString();
    private static readonly string MutexName = $@"Local\larosa023.sift.{Session}";
    private static readonly string PipeName = $"larosa023.sift.{Session}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex m) => _mutex = m;

    public sealed record Request(string Cmd, string? Path = null);
    public sealed record Reply(bool Ok, string? Reason = null, int Pid = 0);

    /// <summary>Claims the instance. Null when another Sift is already running.</summary>
    public static SingleInstance? TryClaim(TimeSpan wait = default)
    {
        var m = new Mutex(false, MutexName);
        try
        {
            if (m.WaitOne(wait)) return new SingleInstance(m);
        }
        catch (AbandonedMutexException) { return new SingleInstance(m); } // previous copy crashed: it's ours now
        m.Dispose();
        return null;
    }

    /// <summary>Serves requests from later launches. <paramref name="handle"/> runs on the caller's thread of choice.</summary>
    public void Serve(Func<Request, Task<Reply>> handle)
    {
        var t = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    var line = reader.ReadLine();
                    var req = line is null ? null : JsonSerializer.Deserialize<Request>(line);
                    var reply = req is null ? new Reply(false, "bad request") : handle(req).GetAwaiter().GetResult();
                    writer.WriteLine(JsonSerializer.Serialize(reply with { Pid = Environment.ProcessId }));
                    server.WaitForPipeDrain();
                }
                catch (OperationCanceledException) { break; }
                catch { Thread.Sleep(200); /* client went away mid-request; keep serving */ }
            }
        }) { IsBackground = true, Name = "Sift instance pipe" };
        t.Start();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Sends a request to the running Sift. Null if it couldn't be reached.</summary>
    public static Reply? Send(Request req, int timeoutMs = 5000)
    {
        try
        {
            AllowSetForegroundWindow(-1); // let the running copy bring its window to the front
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(req));
            var line = reader.ReadLine();
            return line is null ? null : JsonSerializer.Deserialize<Reply>(line);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }
}
