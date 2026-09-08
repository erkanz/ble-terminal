using System.IO.Ports;

namespace BLESerialTerminal;

internal sealed class UsbSerialTransport : IDisposable
{
    private readonly object _sync = new();
    private SerialPort? _port;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Faulted;

    public bool IsOpen
    {
        get
        {
            lock (_sync)
                return _port?.IsOpen == true;
        }
    }

    public string PortName
    {
        get
        {
            lock (_sync)
                return _port?.PortName ?? string.Empty;
        }
    }

    public int BaudRate
    {
        get
        {
            lock (_sync)
                return _port?.BaudRate ?? 0;
        }
    }

    public static IReadOnlyList<string> GetPortNames() =>
        SerialPort.GetPortNames()
            .OrderBy(PortSortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Open(string portName, int baudRate)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("A COM port must be selected.", nameof(portName));
        if (baudRate < 1200 || baudRate > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(baudRate), "Baud rate must be between 1200 and 4000000.");

        Close();

        var port = new SerialPort(portName.Trim(), baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 250,
            WriteTimeout = 2000,
            DtrEnable = false,
            RtsEnable = false
        };

        port.DataReceived += Port_DataReceived;
        port.ErrorReceived += Port_ErrorReceived;

        try
        {
            port.Open();
            lock (_sync)
                _port = port;
        }
        catch
        {
            port.DataReceived -= Port_DataReceived;
            port.ErrorReceived -= Port_ErrorReceived;
            port.Dispose();
            throw;
        }
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
            return Task.CompletedTask;

        byte[] payload = data.ToArray();
        return Task.Run(() =>
        {
            lock (_sync)
            {
                if (_port?.IsOpen != true)
                    throw new InvalidOperationException("USB serial port is not connected.");
                _port.Write(payload, 0, payload.Length);
            }
        });
    }

    public void Close()
    {
        SerialPort? port;
        lock (_sync)
        {
            port = _port;
            _port = null;
        }

        if (port == null)
            return;

        try { port.DataReceived -= Port_DataReceived; } catch { }
        try { port.ErrorReceived -= Port_ErrorReceived; } catch { }
        try { if (port.IsOpen) port.Close(); } catch { }
        try { port.Dispose(); } catch { }
    }

    public void Dispose() => Close();

    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port)
            return;

        try
        {
            int available = port.BytesToRead;
            if (available <= 0)
                return;

            byte[] buffer = new byte[Math.Min(available, 8192)];
            int read = port.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                return;

            if (read != buffer.Length)
                Array.Resize(ref buffer, read);
            DataReceived?.Invoke(buffer);
        }
        catch (InvalidOperationException)
        {
            // Port was closed while a DataReceived callback was already queued.
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex.Message);
        }
    }

    private void Port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e) =>
        Faulted?.Invoke($"Serial error: {e.EventType}");

    private static string PortSortKey(string portName)
    {
        if (portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(portName.AsSpan(3), out int number))
            return $"COM{number:D8}";
        return portName;
    }
}
