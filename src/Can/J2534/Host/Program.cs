using IxxatCanTool.Can;
using IxxatCanTool.Can.J2534;
using IxxatCanTool.Tcp;

// 32-bit J2534 bridge host. Args: --dll <path> --baud <n>. Speaks J2534BridgeProtocol over
// stdout/stdin to the parent app; stderr carries any human-readable diagnostics. stdout is kept
// strictly binary — nothing else may write to it.

Stream stdout = Console.OpenStandardOutput();
Stream stdin = Console.OpenStandardInput();

string? dll = null;
uint baud = 500000;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--dll") dll = args[i + 1];
    else if (args[i] == "--baud" && uint.TryParse(args[i + 1], out uint b)) baud = b;
}

if (string.IsNullOrWhiteSpace(dll))
{
    J2534BridgeProtocol.WriteError(stdout, "no --dll argument supplied to the bridge host");
    return 2;
}

var channel = new J2534CanChannel();

// RX from the bus -> parent. Swallow write failures: the parent going away closes stdin, which
// ends the TX loop below and shuts us down cleanly.
channel.FrameReceived += frame =>
{
    try { J2534BridgeProtocol.WriteFrame(stdout, RawCanWire.Encode(frame)); }
    catch { /* parent gone */ }
};
channel.Error += msg =>
{
    try { J2534BridgeProtocol.WriteError(stdout, msg); } catch { /* parent gone */ }
};

try
{
    channel.Open(dll!, baud);
}
catch (Exception ex)
{
    J2534BridgeProtocol.WriteError(stdout, ex.Message);
    channel.Dispose();
    return 1;
}

J2534BridgeProtocol.WriteReady(stdout);

// Relay TX from the parent until it closes stdin (Disconnect), then tear down the session.
try
{
    while (J2534BridgeProtocol.Read(stdin) is { } msg)
    {
        if (msg.Type == J2534BridgeProtocol.Frame && msg.Payload.Length == RawCanWire.FrameSize)
        {
            CanFrame f = RawCanWire.Decode(msg.Payload);
            try { channel.Send(f.Identifier, f.IsExtended, f.Data); }
            catch (Exception ex) { J2534BridgeProtocol.WriteError(stdout, "TX failed: " + ex.Message); }
        }
    }
}
catch { /* stream error => shutting down */ }

channel.Dispose();
return 0;
