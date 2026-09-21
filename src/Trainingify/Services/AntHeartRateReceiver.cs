#if WINDOWS
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace Trainingify.Services;

// ANT+ HRM receive-only channel: device type 120, 2457 MHz, period 8070.
// USB framing and channel setup follow the ANT Message Protocol; no trainer control.
public sealed class AntHeartRateReceiver : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private readonly object _gate = new();
    public event Action<ushort, int>? Measurement;
    public event Action<string>? Status;

    public void Start(ushort deviceNumber = 0)
    {
        lock (_gate)
        {
            try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            var previous = _worker;
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _worker = Task.Run(async () =>
            {
                await previous;
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        try { Receive(deviceNumber, cancellation.Token); }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            if (cancellation.IsCancellationRequested) break;
                            Status?.Invoke($"ANT+ : {ex.Message} Nouvelle tentative dans 5 s…");
                        }
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally { cancellation.Dispose(); }
            });
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            _cancellation = null;
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            Stop();
            return _worker;
        }
    }

    private void Receive(ushort requestedDevice, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        UsbDevice? usb = null;
        UsbEndpointReader? reader = null;
        UsbEndpointWriter? writer = null;
        var claimed = false;
        try
        {
            usb = UsbDevice.OpenUsbDevice(new UsbDeviceFinder(0x0FCF, 0x1008))
                ?? UsbDevice.OpenUsbDevice(new UsbDeviceFinder(0x0FCF, 0x1009));
            if (usb is null) throw new IOException("Clé USB absente ou inaccessible. Vérifiez son pilote et fermez les autres applications utilisant ANT+.");
            if (usb is IUsbDevice whole)
            {
                if (!whole.SetConfiguration(1) || !whole.ClaimInterface(0))
                    throw new IOException("Impossible de réserver la clé USB ANT+.");
                claimed = true;
            }
            reader = usb.OpenEndpointReader(ReadEndpointID.Ep01);
            writer = usb.OpenEndpointWriter(WriteEndpointID.Ep01);
            var pending = new List<byte>();
            ushort acquired = requestedDevice;
            DateTime lastData = DateTime.UtcNow;
            DateTime lastIdRequest = DateTime.MinValue;
            bool stale = false;

            void Write(byte id, params byte[] data)
            {
                var frame = AntHeartRateProtocol.Frame(id, data);
                var result = writer.Write(frame, 1000, out var written);
                if (result != ErrorCode.None || written != frame.Length)
                    throw new IOException($"Écriture USB : {result}.");
            }

            void Handle(byte id, byte[] data)
            {
                if (id == 0x51 && data.Length >= 5 && (data[3] & 0x7F) == 120)
                    acquired = (ushort)(data[1] | data[2] << 8);
                if (id != 0x4E || data.Length < 9) return;
                lastData = DateTime.UtcNow;
                if (acquired == 0)
                {
                    if (lastData - lastIdRequest > TimeSpan.FromSeconds(1))
                    {
                        Write(0x4D, 0, 0x51);
                        lastIdRequest = lastData;
                    }
                    return;
                }
                if (stale) { Status?.Invoke("ANT+ : signal reçu"); stale = false; }
                Measurement?.Invoke(acquired, data[8]);
            }

            bool Read(byte? expected = null)
            {
                token.ThrowIfCancellationRequested();
                var buffer = new byte[256];
                var result = reader.Read(buffer, 250, out var count);
                if (result != ErrorCode.None && result != ErrorCode.IoTimedOut)
                    throw new IOException($"Lecture USB : {result}. Rebranchez la clé et relancez la recherche.");
                pending.AddRange(buffer.Take(count));
                var accepted = false;
                while (AntHeartRateProtocol.TryRead(pending, out var id, out var data))
                {
                    if (id == 0x40 && data.Length >= 3 && data[1] == expected)
                    {
                        if (data[2] != 0) throw new IOException($"Commande ANT+ 0x{expected:X2} refusée (0x{data[2]:X2}).");
                        accepted = true;
                    }
                    Handle(id, data);
                }
                return accepted;
            }

            void Command(byte id, params byte[] data)
            {
                Write(id, data);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline) if (Read(id)) return;
                throw new IOException($"Pas de réponse à la commande ANT+ 0x{id:X2}.");
            }

            Status?.Invoke("ANT+ : clé USB détectée, initialisation…");
            // A previous process may have exited with the radio channel still open.
            // Drain its pending data before writing: ANT USB2 can otherwise stall OUT.
            Drain(reader);
            Write(0x4A, 0); // Reset this application's dedicated ANT USB receiver.
            if (token.WaitHandle.WaitOne(600)) token.ThrowIfCancellationRequested();
            Command(0x46, 0, 0xB9, 0xA5, 0x21, 0xFB, 0xBD, 0x72, 0xC3, 0x45);
            Command(0x42, 0, 0, 0); // Receive channel, network 0.
            Command(0x51, 0, (byte)requestedDevice, (byte)(requestedDevice >> 8), 120, 0);
            Command(0x43, 0, 0x86, 0x1F); // 8070 / 32768 seconds.
            Command(0x45, 0, 57);
            Command(0x44, 0, 255); // Continue searching after a temporary signal loss.
            Command(0x4B, 0);
            Status?.Invoke("ANT+ : recherche cardio — activez la diffusion sur la montre");
            while (!token.IsCancellationRequested)
            {
                Read();
                if (!stale && DateTime.UtcNow - lastData > TimeSpan.FromSeconds(5))
                {
                    stale = true;
                    Status?.Invoke("ANT+ : en attente du signal cardio");
                }
            }
        }
        finally
        {
            if (reader is not null && writer is not null)
            {
                try
                {
                    Drain(reader);
                    writer.Write(AntHeartRateProtocol.Frame(0x4C, [0]), 500, out _);
                    Drain(reader);
                    writer.Write(AntHeartRateProtocol.Frame(0x41, [0]), 500, out _);
                    Drain(reader);
                }
                catch { /* A disconnected stick cannot acknowledge channel shutdown. */ }
            }
            reader?.Dispose();
            writer?.Dispose();
            if (claimed && usb is IUsbDevice whole) whole.ReleaseInterface(0);
            usb?.Close();
        }
    }

    private static void Drain(UsbEndpointReader reader)
    {
        var buffer = new byte[256];
        var deadline = DateTime.UtcNow.AddMilliseconds(750);
        while (DateTime.UtcNow < deadline)
        {
            var result = reader.Read(buffer, 100, out var count);
            if (result != ErrorCode.None || count == 0) break;
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
#endif
