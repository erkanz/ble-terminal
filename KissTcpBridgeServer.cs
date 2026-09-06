using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace BLESerialTerminal;

internal sealed record KissTcpBridgeOptions(
    IPAddress BindAddress,
    int Port,
    int ClientQueueCapacity = 128,
    int ReadBufferBytes = 4096,
    int Backlog = 8)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BindAddress);
        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "TCP port must be from 0 to 65535.");
        if (ClientQueueCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(ClientQueueCapacity));
        if (ReadBufferBytes is < 64 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(ReadBufferBytes));
        if (Backlog is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(Backlog));
    }
}

internal sealed record KissTcpBridgeBleWriteResult(bool Success, int BytesWritten, string Message)
{
    public static KissTcpBridgeBleWriteResult Ok(int bytes, string message = "Success") => new(true, bytes, message);
    public static KissTcpBridgeBleWriteResult Fail(string message) => new(false, 0, message);
}

internal enum KissTcpBridgeEventKind
{
    State,
    Client,
    Warning,
    Error
}

internal sealed record KissTcpBridgeEvent(DateTime Timestamp, KissTcpBridgeEventKind Kind, string Message);

internal sealed record KissTcpBridgeStats(
    bool Running,
    bool BleAvailable,
    string Endpoint,
    int Clients,
    long BleRxBytes,
    long TcpTxBytes,
    long TcpRxBytes,
    long BleTxBytes,
    long BleRxFrames,
    long TcpRxFrames,
    long MalformedFrames,
    long RejectedWrites,
    long BackpressureDisconnects)
{
    public static KissTcpBridgeStats Empty { get; } = new(
        false, false, "-", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Local TCP KISS transport. TCP byte boundaries are deliberately not treated as KISS
/// frame boundaries: bytes are forwarded unchanged and independent KissStreamDecoder
/// instances are used only for diagnostics/counters. BLE RX fan-out uses a bounded queue
/// per client; a client that cannot keep up is disconnected rather than allowing
/// unbounded memory growth.
/// </summary>
internal sealed class KissTcpBridgeServer : IAsyncDisposable
{
    private readonly Func<byte[], CancellationToken, Task<KissTcpBridgeBleWriteResult>> _bleWriter;
    private readonly ConcurrentDictionary<long, ClientSession> _clients = new();
    private readonly object _stateSync = new();
    private readonly object _bleDecoderSync = new();
    private KissStreamDecoder _bleRxDecoder = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private KissTcpBridgeOptions? _options;
    private string _endpoint = "-";
    private string _bleConnectionKey = string.Empty;
    private bool _bleAvailable;
    private int _running;
    private long _nextClientId;
    private long _bleRxBytes;
    private long _tcpTxBytes;
    private long _tcpRxBytes;
    private long _bleTxBytes;
    private long _bleRxFrames;
    private long _tcpRxFrames;
    private long _malformedFrames;
    private long _rejectedWrites;
    private long _backpressureDisconnects;

    public KissTcpBridgeServer(Func<byte[], CancellationToken, Task<KissTcpBridgeBleWriteResult>> bleWriter)
    {
        _bleWriter = bleWriter ?? throw new ArgumentNullException(nameof(bleWriter));
    }

    public event Action<KissTcpBridgeEvent>? EventOccurred;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public KissTcpBridgeStats GetStats()
    {
        string endpoint;
        bool bleAvailable;
        lock (_stateSync)
        {
            endpoint = _endpoint;
            bleAvailable = _bleAvailable;
        }

        return new KissTcpBridgeStats(
            IsRunning,
            bleAvailable,
            endpoint,
            _clients.Count,
            Interlocked.Read(ref _bleRxBytes),
            Interlocked.Read(ref _tcpTxBytes),
            Interlocked.Read(ref _tcpRxBytes),
            Interlocked.Read(ref _bleTxBytes),
            Interlocked.Read(ref _bleRxFrames),
            Interlocked.Read(ref _tcpRxFrames),
            Interlocked.Read(ref _malformedFrames),
            Interlocked.Read(ref _rejectedWrites),
            Interlocked.Read(ref _backpressureDisconnects));
    }

    public async Task StartAsync(KissTcpBridgeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("KISS TCP bridge is already running.");

        try
        {
            _options = options;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new TcpListener(options.BindAddress, options.Port);
            _listener.Start(options.Backlog);
            lock (_stateSync)
                _endpoint = (_listener.LocalEndpoint as IPEndPoint)?.ToString() ?? $"{options.BindAddress}:{options.Port}";
            Emit(KissTcpBridgeEventKind.State, $"LISTENING {GetStats().Endpoint}");
            _acceptTask = AcceptLoopAsync(_cts.Token);
            await Task.Yield();
        }
        catch
        {
            Interlocked.Exchange(ref _running, 0);
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            lock (_stateSync)
                _endpoint = "-";
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        CancellationTokenSource? cts = _cts;
        TcpListener? listener = _listener;
        Task? acceptTask = _acceptTask;
        _cts = null;
        _listener = null;
        _acceptTask = null;

        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        CloseAllClients("bridge stopped");

        if (acceptTask != null)
        {
            try { await acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        cts?.Dispose();
        lock (_stateSync)
            _endpoint = "-";
        Emit(KissTcpBridgeEventKind.State, "STOPPED");
    }

    public void SetBleAvailable(bool available, string connectionKey, string reason)
    {
        bool stateChanged;
        bool connectionChanged;
        lock (_stateSync)
        {
            connectionKey ??= string.Empty;
            connectionChanged = available && _bleAvailable &&
                                !string.Equals(_bleConnectionKey, connectionKey, StringComparison.Ordinal);
            stateChanged = _bleAvailable != available || connectionChanged;
            _bleAvailable = available;
            _bleConnectionKey = available ? connectionKey : string.Empty;
        }

        if (!stateChanged)
            return;

        lock (_bleDecoderSync)
            _bleRxDecoder = new KissStreamDecoder();

        if (!available || connectionChanged)
            CloseAllClients(connectionChanged ? "BLE connection context changed" : "BLE unavailable");

        string suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})";
        Emit(KissTcpBridgeEventKind.State,
            available ? $"BLE FORWARDING READY{suffix}" : $"BLE FORWARDING SUSPENDED{suffix}");
    }

    public void BroadcastBleRx(byte[] data)
    {
        if (data == null || data.Length == 0 || !IsRunning || !IsBleAvailable())
            return;

        byte[] immutable = data.ToArray();
        Interlocked.Add(ref _bleRxBytes, immutable.Length);

        lock (_bleDecoderSync)
        {
            foreach (KissFrame frame in _bleRxDecoder.Push(immutable))
            {
                Interlocked.Increment(ref _bleRxFrames);
                if (frame.IsMalformed)
                {
                    Interlocked.Increment(ref _malformedFrames);
                    Emit(KissTcpBridgeEventKind.Warning,
                        $"Malformed BLE->TCP KISS frame preserved: {string.Join("; ", frame.Warnings)}");
                }
            }
        }

        foreach (ClientSession session in _clients.Values)
        {
            if (session.TryQueue(immutable))
                continue;

            Interlocked.Increment(ref _backpressureDisconnects);
            Emit(KissTcpBridgeEventKind.Warning,
                $"Client {session.RemoteEndpoint} disconnected: bounded BLE->TCP queue full.");
            session.Close();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsRunning)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || !IsRunning)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested || !IsRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                Emit(KissTcpBridgeEventKind.Error, $"Accept failed: {ex.Message}");
                if (!IsRunning)
                    break;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            client.NoDelay = true;
            if (!IsBleAvailable())
            {
                Emit(KissTcpBridgeEventKind.Warning,
                    $"Rejected TCP client {client.Client.RemoteEndPoint}: BLE forwarding is suspended.");
                try { client.Close(); } catch { }
                continue;
            }

            long id = Interlocked.Increment(ref _nextClientId);
            int queueCapacity = _options?.ClientQueueCapacity ?? 128;
            int readBufferBytes = _options?.ReadBufferBytes ?? 4096;
            var session = new ClientSession(this, id, client, queueCapacity, readBufferBytes, cancellationToken);
            if (!_clients.TryAdd(id, session))
            {
                session.Close();
                continue;
            }

            Emit(KissTcpBridgeEventKind.Client, $"CONNECTED {session.RemoteEndpoint}");
            _ = session.RunAsync();
        }
    }

    private bool IsBleAvailable()
    {
        lock (_stateSync)
            return _bleAvailable;
    }

    private async Task<bool> ForwardTcpToBleAsync(ClientSession session, byte[] data, CancellationToken cancellationToken)
    {
        if (!IsBleAvailable())
        {
            Interlocked.Increment(ref _rejectedWrites);
            Emit(KissTcpBridgeEventKind.Warning,
                $"TCP->BLE rejected for {session.RemoteEndpoint}: BLE forwarding is suspended.");
            return false;
        }

        Interlocked.Add(ref _tcpRxBytes, data.Length);
        foreach (KissFrame frame in session.Decode(data))
        {
            Interlocked.Increment(ref _tcpRxFrames);
            if (frame.IsMalformed)
            {
                Interlocked.Increment(ref _malformedFrames);
                Emit(KissTcpBridgeEventKind.Warning,
                    $"Malformed TCP->BLE KISS frame preserved from {session.RemoteEndpoint}: {string.Join("; ", frame.Warnings)}");
            }
        }

        KissTcpBridgeBleWriteResult result;
        try
        {
            result = await _bleWriter(data, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _rejectedWrites);
            Emit(KissTcpBridgeEventKind.Error,
                $"TCP->BLE write exception for {session.RemoteEndpoint}: {ex.Message}");
            return false;
        }

        if (!result.Success)
        {
            Interlocked.Increment(ref _rejectedWrites);
            Emit(KissTcpBridgeEventKind.Warning,
                $"TCP->BLE write rejected for {session.RemoteEndpoint}: {result.Message}");
            return false;
        }

        Interlocked.Add(ref _bleTxBytes, result.BytesWritten);
        return true;
    }

    private void ClientWroteBytes(int count)
    {
        if (count > 0)
            Interlocked.Add(ref _tcpTxBytes, count);
    }

    private void ClientClosed(ClientSession session)
    {
        if (_clients.TryRemove(session.Id, out _))
            Emit(KissTcpBridgeEventKind.Client, $"DISCONNECTED {session.RemoteEndpoint}");
    }

    private void CloseAllClients(string reason)
    {
        ClientSession[] sessions = _clients.Values.ToArray();
        foreach (ClientSession session in sessions)
            session.Close();
        if (sessions.Length > 0)
            Emit(KissTcpBridgeEventKind.Client, $"Closed {sessions.Length} TCP client(s): {reason}.");
    }

    private void Emit(KissTcpBridgeEventKind kind, string message)
    {
        try { EventOccurred?.Invoke(new KissTcpBridgeEvent(DateTime.Now, kind, message)); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private sealed class ClientSession
    {
        private readonly KissTcpBridgeServer _owner;
        private readonly TcpClient _client;
        private readonly Channel<byte[]> _sendQueue;
        private readonly CancellationTokenSource _cts;
        private readonly int _readBufferBytes;
        private readonly KissStreamDecoder _decoder = new();
        private int _closed;

        public ClientSession(
            KissTcpBridgeServer owner,
            long id,
            TcpClient client,
            int queueCapacity,
            int readBufferBytes,
            CancellationToken serverCancellation)
        {
            _owner = owner;
            Id = id;
            _client = client;
            _readBufferBytes = readBufferBytes;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? $"client-{id}";
        }

        public long Id { get; }
        public string RemoteEndpoint { get; }

        public bool TryQueue(byte[] data) =>
            Volatile.Read(ref _closed) == 0 && _sendQueue.Writer.TryWrite(data);

        public IEnumerable<KissFrame> Decode(byte[] data) => _decoder.Push(data);

        public async Task RunAsync()
        {
            Task readTask = ReadLoopAsync();
            Task writeTask = WriteLoopAsync();
            try
            {
                await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
            }
            finally
            {
                Close();
                try { await Task.WhenAll(readTask, writeTask).ConfigureAwait(false); } catch { }
                _owner.ClientClosed(this);
                _cts.Dispose();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            try { _cts.Cancel(); } catch { }
            _sendQueue.Writer.TryComplete();
            try { _client.Close(); } catch { }
        }

        private async Task ReadLoopAsync()
        {
            byte[] buffer = new byte[_readBufferBytes];
            NetworkStream stream = _client.GetStream();
            while (!_cts.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                if (read <= 0)
                    break;

                byte[] chunk = buffer.AsSpan(0, read).ToArray();
                if (!await _owner.ForwardTcpToBleAsync(this, chunk, _cts.Token).ConfigureAwait(false))
                    break;
            }
        }

        private async Task WriteLoopAsync()
        {
            NetworkStream stream = _client.GetStream();
            try
            {
                await foreach (byte[] chunk in _sendQueue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    await stream.WriteAsync(chunk.AsMemory(), _cts.Token).ConfigureAwait(false);
                    _owner.ClientWroteBytes(chunk.Length);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }
}
