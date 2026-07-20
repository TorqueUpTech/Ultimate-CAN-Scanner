using System.Diagnostics;
using System.IO;
using System.Text;
using IxxatCanTool.Tcp;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// Reaches a 32-bit J2534 driver (e.g. the Tactrix OpenPort 2.0 <c>op20pt32.dll</c>) that this x64
/// process cannot load in-process. It spawns the bundled 32-bit host (<c>UcsJ2534Host.exe</c>),
/// which loads the driver and runs the CAN session, and exchanges frames with it over the child's
/// redirected stdin/stdout using <see cref="J2534BridgeProtocol"/>. RX frames arrive as
/// <see cref="J2534BridgeProtocol.Frame"/> messages; TX is written the same way.
/// </summary>
internal sealed class J2534BridgeSession : IJ2534Transport
{
    /// <summary>Host executable name, expected next to the app (see <see cref="ResolveHostPath"/>).</summary>
    public const string HostExeName = "UcsJ2534Host.exe";

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly Stopwatch _clock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly StringBuilder _stderr = new();

    private Process? _host;
    private Stream? _toHost;
    private Thread? _reader;
    private volatile bool _running;
    private volatile bool _opened;
    private string? _openError;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? Error;

    public double Elapsed => _clock.Elapsed.TotalSeconds;

    public void Open(string dllPath, uint baud)
    {
        string hostPath = ResolveHostPath();

        var psi = new ProcessStartInfo
        {
            FileName = hostPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--dll");
        psi.ArgumentList.Add(dllPath);
        psi.ArgumentList.Add("--baud");
        psi.ArgumentList.Add(baud.ToString());

        try
        {
            _host = Process.Start(psi) ?? throw new IOException("could not start the process");
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to launch the 32-bit J2534 bridge host '{HostExeName}': {ex.Message}", ex);
        }

        _clock.Restart();
        _toHost = _host.StandardInput.BaseStream;

        // Drain stderr (human-readable diagnostics) so the pipe never blocks and we can quote it on failure.
        _host.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data); };
        _host.BeginErrorReadLine();

        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "J2534-Bridge-Rx" };
        _reader.Start();

        if (!_ready.Wait(OpenTimeout))
        {
            Dispose();
            throw new IOException($"The 32-bit J2534 bridge host did not report ready within {OpenTimeout.TotalSeconds:0}s.{StderrTail()}");
        }
        if (!_opened)
        {
            string msg = _openError ?? "the bridge host exited before opening the device";
            Dispose();
            throw new IOException($"J2534 bridge could not open the driver: {msg}{StderrTail()}");
        }
    }

    private void ReadLoop()
    {
        Stream stdout = _host!.StandardOutput.BaseStream;
        try
        {
            while (_running)
            {
                if (J2534BridgeProtocol.Read(stdout) is not { } msg)
                    break; // host closed stdout

                switch (msg.Type)
                {
                    case J2534BridgeProtocol.Ready:
                        _opened = true;
                        _ready.Set();
                        break;

                    case J2534BridgeProtocol.Error:
                        string text = Encoding.UTF8.GetString(msg.Payload);
                        if (!_opened) _openError = text;      // fault during Open -> reported by Open
                        else if (_running) Error?.Invoke("J2534 bridge: " + text);
                        _ready.Set();
                        break;

                    case J2534BridgeProtocol.Frame when msg.Payload.Length == RawCanWire.FrameSize:
                        CanFrame d = RawCanWire.Decode(msg.Payload);
                        FrameReceived?.Invoke(new CanFrame
                        {
                            TimeStamp = _clock.Elapsed.TotalSeconds,
                            Direction = CanDirection.Rx,
                            Identifier = d.Identifier,
                            IsExtended = d.IsExtended,
                            IsRemote = d.IsRemote,
                            Data = d.Data,
                        });
                        break;
                }
            }
        }
        catch (Exception ex) when (_running)
        {
            if (!_opened) { _openError = ex.Message; _ready.Set(); }
            else Error?.Invoke("J2534 bridge RX stopped: " + ex.Message);
        }
        finally
        {
            // Host stdout ended: if it died after opening, tell the adapter the link is gone.
            if (_running && _opened)
                Error?.Invoke("J2534 bridge host exited." + StderrTail());
            _ready.Set();
        }
    }

    public void Send(uint identifier, bool extended, byte[] data)
    {
        if (_toHost is null)
            throw new InvalidOperationException("Bridge not open.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        byte[] wire = RawCanWire.Encode(new CanFrame
        {
            Identifier = identifier,
            IsExtended = extended,
            Data = data,
        });
        J2534BridgeProtocol.WriteFrame(_toHost, wire);
    }

    public void Dispose()
    {
        _running = false;

        // Closing stdin signals the host to shut down its session and exit cleanly.
        try { _toHost?.Close(); } catch { /* best effort */ }

        try
        {
            if (_host is { HasExited: false })
            {
                if (!_host.WaitForExit(2000))
                    _host.Kill(entireProcessTree: true);
            }
        }
        catch { /* best effort */ }

        if (_reader is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));

        try { _host?.Dispose(); } catch { /* best effort */ }
        _host = null;
        _toHost = null;
        _reader = null;
    }

    private string StderrTail()
    {
        string s;
        lock (_stderr) s = _stderr.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "" : $" (host: {s})";
    }

    /// <summary>Subfolder (next to the app) holding the self-contained 32-bit host + its runtime.</summary>
    public const string HostSubfolder = "j2534host";

    /// <summary>
    /// Find the 32-bit host exe. It ships self-contained in a <see cref="HostSubfolder"/> beside the
    /// app (its own x86 runtime kept apart from the app's x64 files to avoid name collisions); a bare
    /// copy alongside the app is also accepted as a fallback.
    /// </summary>
    private static string ResolveHostPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, HostSubfolder, HostExeName),
            Path.Combine(baseDir, HostExeName),
        ];
        foreach (string c in candidates)
            if (File.Exists(c))
                return c;

        throw new FileNotFoundException(
            $"The 32-bit J2534 bridge host '{HostExeName}' was not found beside the application " +
            $"({baseDir}). Reinstall, or use a 64-bit J2534 driver / the Ixxat / OBDX backend.");
    }
}
