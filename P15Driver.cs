using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;

namespace P15Printer;

/// <summary>
/// Linux driver for the Marklife / Pristar "P15 Mini" Bluetooth LE label printer.
/// Functionally identical to the Windows driver, but the BLE plumbing goes through
/// BlueZ over D-Bus (via the Linux.Bluetooth package) instead of the WinRT
/// Windows.Devices.Bluetooth APIs.
///
/// Protocol (reverse-engineered, see tomLadder/thermoprint REVERSE_ENGINEERING.md):
///   Service   0000ff00-0000-1000-8000-00805f9b34fb
///   RX/Notify 0000ff01-...   printer -> host status bytes
///   TX/Write  0000ff02-...   host -> printer command + raster stream
///   CX/Ctrl   0000ff03-...   printer -> host flow-control credits
///
/// The printer paces the host with a credit scheme over the CX characteristic:
///   [0x01, n]            -> grants 'n' write credits (1 credit == 1 packet)
///   [0x02, lo, hi]       -> advertises MTU (little-endian)
/// Each on-air packet is capped at 95 bytes regardless of the negotiated MTU.
/// </summary>
public sealed class P15Driver : IAsyncDisposable
{
    // BlueZ works with lower-case UUID strings.
    public const string ServiceUuid = "0000ff00-0000-1000-8000-00805f9b34fb";
    public const string RxUuid      = "0000ff01-0000-1000-8000-00805f9b34fb";
    public const string TxUuid      = "0000ff02-0000-1000-8000-00805f9b34fb";
    public const string CxUuid      = "0000ff03-0000-1000-8000-00805f9b34fb";

    private const int PacketSize = 95; // hard cap imposed by the P15 firmware

    private Device? _device;
    private GattCharacteristic? _tx;
    private GattCharacteristic? _rx;
    private GattCharacteristic? _cx;

    // Write-without-response option for the TX characteristic. BlueZ maps
    // {"type","command"} to Write Command (no ATT response), matching the
    // GattWriteOption.WriteWithoutResponse the Windows driver uses.
    private static readonly Dictionary<string, object> WriteCommandOptions =
        new() { { "type", "command" } };

    // Credit-based flow control. Seeded with a small budget so the very first
    // packets can flow before the printer issues its first credit grant; the
    // semaphore is then topped up from CX notifications.
    private readonly SemaphoreSlim _credits = new(initialCount: 8, maxCount: 1024);

    /// <summary>Raised for every status byte pair the printer reports on RX.</summary>
    public event Action<P15Status>? StatusReported;

    /// <summary>
    /// Finds the printer by its BLE printer service (not by name — these printers
    /// advertise under varied names like "Marklife"/"Pristar"/digits) and connects.
    /// When several devices expose the service, a non-null <paramref name="nameHint"/>
    /// is used only to break ties.
    /// </summary>
    public static async Task<P15Driver> ConnectAsync(string? nameHint = null, int timeoutMs = 15000)
    {
        var adapter = await GetAdapterAsync();

        // Discover so freshly-woken / unpaired printers show up, then read the
        // known-device list (paired + just-discovered).
        await DiscoverAsync(adapter, Math.Min(timeoutMs, 8000));
        var devices = await adapter.GetDevicesAsync();
        if (devices.Count == 0)
            throw new InvalidOperationException(
                "No BLE devices found. Turn the printer on and awake, make sure no other app " +
                "(e.g. the phone) is connected to it, and that the adapter is up " +
                "(rfkill unblock bluetooth; systemctl status bluetooth).");

        // Prefer devices actually advertising the P15 service. If none advertise it
        // (some firmwares only expose it after connect), fall back to a name hint,
        // then to probing everything.
        var withService = new List<Device>();
        var nameMatch = new List<Device>();
        foreach (var d in devices)
        {
            DeviceProperties p;
            try { p = await d.GetAllAsync(); } catch { continue; }

            var uuids = p.UUIDs ?? Array.Empty<string>();
            if (uuids.Any(u => u.Equals(ServiceUuid, StringComparison.OrdinalIgnoreCase)))
            {
                withService.Add(d);
            }
            else if (nameHint is not null)
            {
                var name = p.Name ?? p.Alias ?? "";
                if (name.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
                    nameMatch.Add(d);
            }
        }

        IReadOnlyList<Device> candidates =
            withService.Count > 0 ? withService :
            nameMatch.Count  > 0 ? nameMatch    :
            devices;

        Exception? lastError = null;
        foreach (var device in candidates)
        {
            try
            {
                var driver = new P15Driver();
                await driver.OpenAsync(device, timeoutMs);   // throws if the service is absent
                return driver;
            }
            catch (Exception ex)
            {
                lastError = ex;                              // keep the best candidate's reason
                try { await device.DisconnectAsync(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            $"Found {devices.Count} BLE device(s) but could not open the P15. " +
            $"Last reason: {lastError?.Message ?? "unknown"}", lastError);
    }

    /// <summary>A Bluetooth LE device discovered during a scan.</summary>
    public readonly record struct ScanResult(string Name, string Address, bool HasPrinterService);

    /// <summary>
    /// Enumerates nearby/paired BLE devices for diagnostics. Marks which ones
    /// advertise the P15 printer service — useful to confirm the printer is
    /// reachable over BLE.
    /// </summary>
    public static async Task<IReadOnlyList<ScanResult>> ScanAsync(int durationMs = 6000)
    {
        var adapter = await GetAdapterAsync();
        await DiscoverAsync(adapter, durationMs);

        var devices = await adapter.GetDevicesAsync();
        var results = new List<ScanResult>();
        foreach (var d in devices)
        {
            DeviceProperties p;
            try { p = await d.GetAllAsync(); } catch { continue; }

            string name = !string.IsNullOrWhiteSpace(p.Name) ? p.Name!
                        : !string.IsNullOrWhiteSpace(p.Alias) ? p.Alias!
                        : "(no name)";
            bool hasService = (p.UUIDs ?? Array.Empty<string>())
                .Any(u => u.Equals(ServiceUuid, StringComparison.OrdinalIgnoreCase));

            results.Add(new ScanResult(name, p.Address ?? "", hasService));
        }
        return results;
    }

    // ----------------------------------------------------------------------
    //  Connection setup
    // ----------------------------------------------------------------------

    private static async Task<Adapter> GetAdapterAsync()
    {
        var adapters = await BlueZManager.GetAdaptersAsync();
        var adapter = adapters.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No Bluetooth adapter found. Is BlueZ running and the adapter powered? " +
                "(bluetoothctl power on; systemctl status bluetooth).");
        return adapter;
    }

    private static async Task DiscoverAsync(Adapter adapter, int durationMs)
    {
        try { await adapter.StartDiscoveryAsync(); } catch { /* already discovering / no perms */ }
        await Task.Delay(durationMs);
        try { await adapter.StopDiscoveryAsync(); } catch { /* ignore */ }
    }

    private async Task OpenAsync(Device device, int timeoutMs)
    {
        _device = device;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 5000));

        // Connect (pairing is usually "Just Works" and often unnecessary for these
        // printers). If the first attempt fails, try to bond then reconnect.
        try
        {
            await device.ConnectAsync();
        }
        catch
        {
            try { await device.PairAsync(); } catch { /* may already be paired / not required */ }
            await device.ConnectAsync();
        }

        await device.WaitForPropertyValueAsync("Connected", value: true, timeout);
        // BlueZ resolves GATT asynchronously after connect; wait for it before querying.
        await device.WaitForPropertyValueAsync("ServicesResolved", value: true, timeout);

        // The service can lag ServicesResolved on a cold link — retry briefly.
        IGattService1? service = null;
        for (int i = 0; i < 20 && service is null; i++)
        {
            service = await device.GetServiceAsync(ServiceUuid);
            if (service is null) await Task.Delay(300);
        }
        if (service is null)
            throw new InvalidOperationException(
                "P15 service not found on this device. Make sure the printer is on, awake, " +
                "and not connected to another app (e.g. the phone).");

        _tx = await GetCharacteristicAsync(service, TxUuid);
        _rx = await GetCharacteristicAsync(service, RxUuid);
        _cx = await GetCharacteristicAsync(service, CxUuid);

        // Subscribe to status (RX) and flow-control credits (CX).
        _rx.Value += OnRxValueAsync;
        await _rx.StartNotifyAsync();

        _cx.Value += OnCxValueAsync;
        await _cx.StartNotifyAsync();
    }

    private static async Task<GattCharacteristic> GetCharacteristicAsync(IGattService1 service, string uuid)
    {
        var ch = await service.GetCharacteristicAsync(uuid);
        return ch ?? throw new InvalidOperationException($"Characteristic {uuid} not found.");
    }

    private Task OnRxValueAsync(GattCharacteristic _, GattCharacteristicValueEventArgs e)
    {
        var data = e.Value;
        if (data.Length >= 2)
            StatusReported?.Invoke(new P15Status(data[0], data[1]));
        else if (data.Length == 1)
            StatusReported?.Invoke(new P15Status(data[0], 0));
        return Task.CompletedTask;
    }

    private Task OnCxValueAsync(GattCharacteristic _, GattCharacteristicValueEventArgs e)
    {
        var data = e.Value;
        if (data.Length >= 2 && data[0] == 0x01)
        {
            // Credit grant: release that many slots (clamped to the semaphore max).
            int n = data[1];
            for (int i = 0; i < n; i++)
            {
                try { _credits.Release(); } catch (SemaphoreFullException) { break; }
            }
        }
        // [0x02, lo, hi] MTU advert is informational; we always chunk to PacketSize.
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------------
    //  High-level print API  (byte-for-byte identical to the Windows driver)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Prints a 1-bit raster page. <paramref name="bitmap"/> must be row-major,
    /// MSB-first packed (bit7 = leftmost pixel, 1 = black), with
    /// <paramref name="widthBytes"/> bytes per row and <paramref name="height"/> rows.
    /// </summary>
    /// <param name="density">Burn darkness 0..15 (typical 8).</param>
    public async Task PrintRasterAsync(byte[] bitmap, int widthBytes, int height, byte density = 8)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (widthBytes <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthBytes));
        if (bitmap.Length < widthBytes * height)
            throw new ArgumentException("Bitmap buffer is smaller than widthBytes * height.");

        // --- Init ---
        await SendAsync(new byte[15]);                               // wakeup (15 zero bytes)
        await SendAsync(new byte[] { 0x10, 0xFF, 0xF1, 0x02 });      // enable printing
        await SendAsync(new byte[] { 0x1F, 0x70, 0x02, density });   // set density

        // --- Raster (GS v 0): 1D 76 30 m xL xH yL yH <data> ---
        var header = new byte[]
        {
            0x1D, 0x76, 0x30, 0x00,
            (byte)(widthBytes & 0xFF), (byte)((widthBytes >> 8) & 0xFF),
            (byte)(height & 0xFF),     (byte)((height >> 8) & 0xFF),
        };
        var payload = new byte[header.Length + widthBytes * height];
        System.Buffer.BlockCopy(header, 0, payload, 0, header.Length);
        System.Buffer.BlockCopy(bitmap, 0, payload, header.Length, widthBytes * height);
        await SendAsync(payload);

        // --- Finish ---
        await SendAsync(new byte[] { 0x1B, 0x4A, 0x64 });            // feed 100 dots to tear-off
        await SendAsync(new byte[] { 0x10, 0xFF, 0xF1, 0x45 });      // stop print job
    }

    /// <summary>Manually advance the label by <paramref name="dots"/> (0..255).</summary>
    public Task FeedAsync(byte dots = 100) =>
        SendAsync(new byte[] { 0x1B, 0x4A, dots });

    /// <summary>Feed to the next gap/black-mark (form feed).</summary>
    public Task FormFeedAsync() =>
        SendAsync(new byte[] { 0x1D, 0x0C });

    /// <summary>
    /// Writes a raw byte stream to TX, split into 95-byte packets and paced
    /// by the printer's credit grants.
    /// </summary>
    public async Task SendAsync(byte[] data)
    {
        if (_tx is null) throw new InvalidOperationException("Not connected.");

        for (int offset = 0; offset < data.Length; offset += PacketSize)
        {
            // Wait for a credit; fall back after a short timeout so an
            // under-talkative firmware can't deadlock the stream.
            await _credits.WaitAsync(2000);   // proceed best-effort on timeout

            int len = Math.Min(PacketSize, data.Length - offset);
            var chunk = new byte[len];
            System.Buffer.BlockCopy(data, offset, chunk, 0, len);

            await _tx.WriteValueAsync(chunk, WriteCommandOptions);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_rx is not null) _rx.Value -= OnRxValueAsync;
            if (_cx is not null) _cx.Value -= OnCxValueAsync;
        }
        catch { /* ignore */ }

        if (_device is not null)
        {
            try { await _device.DisconnectAsync(); } catch { /* ignore */ }
            (_device as IDisposable)?.Dispose();
        }

        _credits.Dispose();
    }
}

/// <summary>A status byte pair reported by the printer on the RX characteristic.</summary>
public readonly record struct P15Status(byte First, byte Second)
{
    public bool IsError => First == 0xFF;
    public bool IsSuccess => First is 0xAA or 0x4F or 0x4B;

    public string Describe() => First switch
    {
        0xFF => Second switch
        {
            0x01 => "Paper out",
            0x02 => "Cover open",
            0x03 => "Overheating",
            0x04 => "Low battery",
            0x05 => "Cover closed",
            _    => $"Error 0x{Second:X2}",
        },
        0xAA or 0x4F or 0x4B => "Print OK",
        _ => $"Status 0x{First:X2} 0x{Second:X2}",
    };
}
