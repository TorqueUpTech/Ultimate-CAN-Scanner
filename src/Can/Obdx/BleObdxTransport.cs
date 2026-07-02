using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace IxxatCanTool.Can.Obdx;

/// <summary>
/// OBDX over Bluetooth Low Energy using the Nordic UART Service (NUS) it exposes:
/// service <c>6E400001-…</c>, the client <b>writes</b> to <c>…0002</c> and subscribes to
/// <b>notifications</b> on <c>…0003</c> (per the OBDX "Wireless Connections" guide). No pairing
/// is required. Async WinRT calls are bridged to the synchronous <see cref="IObdxTransport"/>
/// surface by running them on the thread pool; inbound notification bytes are buffered so the
/// adapter's polling <see cref="Read"/> can drain them.
///
/// Compile-verified only — like the rest of the OBDX backend this has not been run against real
/// hardware. Large writes are chunked to the negotiated MTU; our DVI commands fit one packet.
/// </summary>
public sealed class BleObdxTransport : IObdxTransport
{
    // Nordic UART Service — the profile the OBDX advertises.
    private static readonly Guid NusService = new("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid NusRxWrite = new("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");  // PC → tool
    private static readonly Guid NusTxNotify = new("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"); // tool → PC

    private readonly object _rxLock = new();
    private readonly Queue<byte> _rxQueue = new();
    private readonly ManualResetEventSlim _rxAvailable = new(false);

    private ulong _address; // 0 = discover the first OBDX in range on Open()
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _rxChar;
    private GattCharacteristic? _txChar;
    private int _chunk = 20;

    /// <param name="address">Bluetooth address, or 0 to scan for the first advertising OBDX.</param>
    public BleObdxTransport(ulong address) => _address = address;

    public void Open() => Task.Run(OpenAsync).GetAwaiter().GetResult();

    private async Task OpenAsync()
    {
        if (_address == 0)
        {
            _address = await ScanAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (_address == 0)
                throw new IOException("No OBDX Pro found over BLE (is it powered and in range?).");
        }

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask().ConfigureAwait(false)
                  ?? throw new IOException("BLE device could not be opened.");

        var svc = await _device.GetGattServicesForUuidAsync(NusService, BluetoothCacheMode.Uncached)
                               .AsTask().ConfigureAwait(false);
        if (svc.Status != GattCommunicationStatus.Success || svc.Services.Count == 0)
            throw new IOException($"OBDX BLE service not found ({svc.Status}).");
        var service = svc.Services[0];

        _rxChar = await GetCharacteristic(service, NusRxWrite).ConfigureAwait(false);
        _txChar = await GetCharacteristic(service, NusTxNotify).ConfigureAwait(false);

        try { _chunk = Math.Max(1, service.Session.MaxPduSize - 3); } catch { _chunk = 20; }

        _txChar.ValueChanged += OnNotification;
        var cfg = await _txChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
        if (cfg != GattCommunicationStatus.Success)
            throw new IOException($"Could not enable OBDX BLE notifications ({cfg}).");
    }

    private static async Task<GattCharacteristic> GetCharacteristic(GattDeviceService service, Guid uuid)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached)
                                  .AsTask().ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
            throw new IOException($"OBDX BLE characteristic {uuid} not found ({result.Status}).");
        return result.Characteristics[0];
    }

    /// <summary>Scan for the first device advertising the OBDX name or NUS service; 0 if none.</summary>
    private static Task<ulong> ScanAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<ulong>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) =>
        {
            bool match = (args.Advertisement.LocalName?.Contains("OBDX", StringComparison.OrdinalIgnoreCase) ?? false)
                         || args.Advertisement.ServiceUuids.Contains(NusService);
            if (match)
                tcs.TrySetResult(args.BluetoothAddress);
        };
        watcher.Start();
        _ = Task.Delay(timeout).ContinueWith(_ => tcs.TrySetResult(0));
        return tcs.Task.ContinueWith(t =>
        {
            try { watcher.Stop(); } catch { /* best effort */ }
            return t.Result;
        });
    }

    private void OnNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var buffer = args.CharacteristicValue;
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        lock (_rxLock)
            foreach (byte b in bytes)
                _rxQueue.Enqueue(b);
        _rxAvailable.Set();
    }

    public void Write(byte[] data) => Task.Run(() => WriteAsync(data)).GetAwaiter().GetResult();

    private async Task WriteAsync(byte[] data)
    {
        var option = _rxChar!.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        for (int offset = 0; offset < data.Length; offset += _chunk)
        {
            int len = Math.Min(_chunk, data.Length - offset);
            var writer = new DataWriter();
            writer.WriteBytes(data.AsSpan(offset, len).ToArray());
            await _rxChar.WriteValueAsync(writer.DetachBuffer(), option).AsTask().ConfigureAwait(false);
        }
    }

    public int Read(byte[] buffer)
    {
        if (!_rxAvailable.Wait(150))
            return 0; // idle — let the RX loop re-check its running flag

        lock (_rxLock)
        {
            int n = 0;
            while (n < buffer.Length && _rxQueue.Count > 0)
                buffer[n++] = _rxQueue.Dequeue();
            if (_rxQueue.Count == 0)
                _rxAvailable.Reset();
            return n;
        }
    }

    public void Close()
    {
        if (_txChar is not null)
        {
            try { _txChar.ValueChanged -= OnNotification; } catch { /* best effort */ }
            _txChar = null;
        }
        _rxChar = null;
        try { _device?.Dispose(); } catch { /* best effort */ }
        _device = null;
    }

    public void Dispose()
    {
        Close();
        _rxAvailable.Dispose();
    }
}
