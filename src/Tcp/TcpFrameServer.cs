using System.Net;
using System.Net.Sockets;
using IxxatCanTool.Can;

namespace IxxatCanTool.Tcp;

/// <summary>
/// Serves CAN frames over TCP in the 13-byte <see cref="RawCanWire"/> format on a
/// loopback port, so a replayed log can feed the Can-Display dash sim with no
/// adapter (mirrors the CAN-Replay tool's server). Tracks connected clients and
/// broadcasts each frame to all of them; the playback loop is the only writer, so
/// broadcasts never race each other.
///
/// The link is bidirectional: a per-client reader re-assembles inbound 13-byte
/// RawCanWire frames and raises <see cref="FrameReceived"/> for each, so a client
/// can push traffic into the tool (surfaced as RX). Reading continuously also keeps
/// a client's send buffer from backing up and stalling the broadcaster.
/// </summary>
public sealed class TcpFrameServer : IDisposable
{
    public const string DefaultBindAddress = "127.0.0.1";
    public const int DefaultPort = 51729;

    /// <summary>Human-readable status lines (may fire on background threads).</summary>
    public event Action<string>? Log;

    /// <summary>Raised per inbound RawCanWire frame from any client (fires on background threads).</summary>
    public event Action<CanFrame>? FrameReceived;

    public bool Running { get; private set; }
    public string BindAddress { get; private set; } = DefaultBindAddress;
    public int Port { get; private set; }

    public int ClientCount
    {
        get { lock (_lock) return _clients.Count; }
    }

    private readonly List<TcpClient> _clients = new();
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Bind and begin accepting clients. Throws if the port is already in use.</summary>
    public void Start(string bindAddress = DefaultBindAddress, int port = DefaultPort)
    {
        if (Running) return;

        var addr = IPAddress.Parse(bindAddress);
        _listener = new TcpListener(addr, port);
        _listener.Start();
        BindAddress = bindAddress;
        Port = port;
        _cts = new CancellationTokenSource();
        Running = true;

        _ = AcceptLoopAsync(_listener, _cts.Token);
        Log?.Invoke($"TCP server listening on {bindAddress}:{port}.");
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;

        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* best effort */ }
        CloseAll();

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        Log?.Invoke("TCP server stopped.");
    }

    /// <summary>Write one 13-byte frame to every connected client; drop on error.</summary>
    public void Broadcast(byte[] wire)
    {
        lock (_lock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                try { _clients[i].GetStream().Write(wire, 0, wire.Length); }
                catch { Drop(i); }
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }   // listener stopped or cancelled
            Add(client);
        }
    }

    private void Add(TcpClient client)
    {
        client.NoDelay = true;                 // single-frame latency, like the real server
        lock (_lock) _clients.Add(client);
        Log?.Invoke($"client connected ({Describe(client)}); {ClientCount} attached.");
        _ = ReceiveAsync(client);              // fire-and-forget reader (RX + keeps buffer drained)
    }

    private void CloseAll()
    {
        lock (_lock)
        {
            foreach (var c in _clients)
                try { c.Close(); } catch { /* best effort */ }
            _clients.Clear();
        }
    }

    // Read inbound bytes, re-assemble fixed 13-byte RawCanWire frames across reads, and republish
    // each as RX. Continuous reading also stops the client's send buffer backing up (broadcaster
    // stall). The format is unframed, so byte alignment relies on the client sending whole frames.
    private async Task ReceiveAsync(TcpClient client)
    {
        var buf = new byte[4096];
        var frame = new byte[RawCanWire.FrameSize];
        int have = 0;
        try
        {
            var stream = client.GetStream();
            int n;
            while ((n = await stream.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    frame[have++] = buf[i];
                    if (have < RawCanWire.FrameSize)
                        continue;
                    have = 0;
                    FrameReceived?.Invoke(RawCanWire.Decode(frame));
                }
            }
        }
        catch { /* falls through to removal */ }

        lock (_lock)
        {
            int idx = _clients.IndexOf(client);
            if (idx >= 0) Drop(idx);
        }
    }

    // Caller must hold _lock.
    private void Drop(int index)
    {
        var c = _clients[index];
        _clients.RemoveAt(index);
        try { c.Close(); } catch { /* best effort */ }
        Log?.Invoke($"client disconnected; {_clients.Count} attached.");
    }

    private static string Describe(TcpClient c)
    {
        try { return c.Client.RemoteEndPoint?.ToString() ?? "?"; }
        catch { return "?"; }
    }

    public void Dispose() => Stop();
}
