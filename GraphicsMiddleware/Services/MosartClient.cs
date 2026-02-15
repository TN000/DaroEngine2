using System.Net.Sockets;
using System.Text;

namespace GraphicsMiddleware.Services;

/// <summary>
/// TCP client for forwarding commands to DARO PLAYOUT's MosartServer.
/// This bridges the REST API to the TCP-based Mosart protocol.
///
/// Features:
/// - Persistent TCP connection with automatic reconnection
/// - TCP keepalive for connection health monitoring
/// - Thread-safe for concurrent command execution
/// - Retry logic with exponential backoff
/// - Graceful shutdown via IDisposable
/// </summary>
public interface IMosartClient : IDisposable
{
    Task<string> SendCommandAsync(string itemId, int command, CancellationToken cancellationToken = default);
    Task<string> CueAsync(string itemId, CancellationToken cancellationToken = default);
    Task<string> PlayAsync(string itemId, CancellationToken cancellationToken = default);
    Task<string> StopAsync(string itemId, CancellationToken cancellationToken = default);
    Task<string> PauseAsync(string itemId, CancellationToken cancellationToken = default);
    bool IsConnected { get; }

    /// <summary>
    /// Explicitly connect to the server. Connection is also established automatically on first command.
    /// </summary>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the server and release resources.
    /// </summary>
    Task DisconnectAsync();
}

public class MosartClient : IMosartClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<MosartClient> _logger;

    // Configurable timeouts (loaded from appsettings.json)
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _connectTimeout;

    // Configurable retry settings
    private readonly int _maxRetryAttempts;
    private readonly TimeSpan _baseRetryDelay;

    // Connection state
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _isConnected;
    private int _isDisposedInt; // For Interlocked thread-safe disposal
    private bool _isDisposed => _isDisposedInt != 0;

    // Health monitoring
    private DateTime _lastSuccessfulOperation = DateTime.MinValue;
    private int _consecutiveFailures;

    public MosartClient(IConfiguration configuration, ILogger<MosartClient> logger)
    {
        _host = configuration.GetValue("MosartClient:Host", "127.0.0.1")!;
        _port = configuration.GetValue("MosartClient:Port", 5555);
        _logger = logger;

        // Load timeout configurations with sensible defaults
        _commandTimeout = TimeSpan.FromMilliseconds(configuration.GetValue("MosartClient:CommandTimeoutMs", 1000));
        _connectTimeout = TimeSpan.FromMilliseconds(configuration.GetValue("MosartClient:ConnectTimeoutMs", 1000));
        _maxRetryAttempts = configuration.GetValue("MosartClient:MaxRetryAttempts", 3);
        _baseRetryDelay = TimeSpan.FromMilliseconds(configuration.GetValue("MosartClient:BaseRetryDelayMs", 100));

        _logger.LogInformation(
            "MosartClient configured: {Host}:{Port}, CommandTimeout={CommandTimeout}ms, ConnectTimeout={ConnectTimeout}ms, MaxRetries={MaxRetries}",
            _host, _port, _commandTimeout.TotalMilliseconds, _connectTimeout.TotalMilliseconds, _maxRetryAttempts);
    }

    public bool IsConnected => _isConnected && _tcpClient?.Connected == true;

    public Task<string> CueAsync(string itemId, CancellationToken cancellationToken = default)
        => SendCommandAsync(itemId, 0, cancellationToken);

    public Task<string> PlayAsync(string itemId, CancellationToken cancellationToken = default)
        => SendCommandAsync(itemId, 1, cancellationToken);

    public Task<string> StopAsync(string itemId, CancellationToken cancellationToken = default)
        => SendCommandAsync(itemId, 2, cancellationToken);

    public Task<string> PauseAsync(string itemId, CancellationToken cancellationToken = default)
        => SendCommandAsync(itemId, 4, cancellationToken);

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_isDisposed) return;

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<string> SendCommandAsync(string itemId, int command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var message = $"{itemId}|{command}\r\n";
        _logger.LogDebug("Sending to DARO PLAYOUT: {Message}", message.TrimEnd());

        // Retry loop with exponential backoff
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
        {
            try
            {
                var response = await SendCommandWithConnectionAsync(message, cancellationToken).ConfigureAwait(false);

                // Success - reset failure counter
                _consecutiveFailures = 0;
                _lastSuccessfulOperation = DateTime.UtcNow;

                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User cancellation - don't retry
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _consecutiveFailures++;

                _logger.LogWarning(
                    "Attempt {Attempt}/{MaxAttempts} failed for command to DARO PLAYOUT: {Error}",
                    attempt, _maxRetryAttempts, ex.Message);

                // Mark connection as broken so next attempt will reconnect
                await MarkConnectionBrokenAsync().ConfigureAwait(false);

                if (attempt < _maxRetryAttempts)
                {
                    // Exponential backoff: baseDelay, 2x, 4x, etc.
                    var delay = TimeSpan.FromMilliseconds(_baseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));

                    _logger.LogDebug("Waiting {Delay}ms before retry", delay.TotalMilliseconds);

                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }
        }

        // All retries exhausted
        _logger.LogError(lastException, "All {MaxAttempts} attempts failed for command to DARO PLAYOUT", _maxRetryAttempts);

        return lastException switch
        {
            OperationCanceledException => "ERROR:TIMEOUT",
            SocketException socketEx => $"ERROR:CONNECTION_FAILED:{socketEx.Message}",
            _ => $"ERROR:{lastException?.Message ?? "Unknown error"}"
        };
    }

    private async Task<string> SendCommandWithConnectionAsync(string message, CancellationToken cancellationToken)
    {
        // Acquire send lock to ensure thread-safe command execution
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Ensure we have a valid connection
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_commandTimeout);

            // Send command
            var bytes = Encoding.UTF8.GetBytes(message);
            await _networkStream!.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await _networkStream.FlushAsync(cts.Token).ConfigureAwait(false);

            // Read response
            var buffer = new byte[1024];
            var responseBuilder = new StringBuilder();

            // Read until we get a complete line (ending with \r\n or \n)
            while (true)
            {
                var bytesRead = await _networkStream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    // Connection closed by server
                    throw new IOException("Connection closed by server");
                }

                var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                responseBuilder.Append(chunk);

                if (chunk.Contains('\n')) break;
            }

            var response = responseBuilder.ToString().Trim();
            _logger.LogDebug("Response from DARO PLAYOUT: {Response}", response);

            return response;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        // Fast path: already connected
        if (IsConnected)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (IsConnected)
            {
                return;
            }

            // Close any stale connection
            await CloseConnectionAsync().ConfigureAwait(false);

            // Establish new connection
            if (!await ConnectInternalAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new SocketException((int)SocketError.ConnectionRefused);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task<bool> ConnectInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Establishing connection to DARO PLAYOUT at {Host}:{Port}", _host, _port);

            _tcpClient = new TcpClient();

            // Configure TCP keepalive for connection health monitoring
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // On Windows, configure keepalive timing (send probe after 30s idle, then every 5s)
            // This helps detect dead connections faster
            ConfigureKeepalive(_tcpClient.Client);

            // Set socket options for better responsiveness
            _tcpClient.NoDelay = true; // Disable Nagle's algorithm for lower latency
            _tcpClient.ReceiveTimeout = (int)_commandTimeout.TotalMilliseconds;
            _tcpClient.SendTimeout = (int)_commandTimeout.TotalMilliseconds;

            // Connect with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_connectTimeout);

            await _tcpClient.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);

            _networkStream = _tcpClient.GetStream();
            _networkStream.ReadTimeout = (int)_commandTimeout.TotalMilliseconds;
            _networkStream.WriteTimeout = (int)_commandTimeout.TotalMilliseconds;

            _isConnected = true;
            _logger.LogInformation("Connected to DARO PLAYOUT at {Host}:{Port}", _host, _port);

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Connection to DARO PLAYOUT timed out");
            await CloseConnectionAsync().ConfigureAwait(false);
            return false;
        }
        catch (SocketException ex)
        {
            _logger.LogWarning("Cannot connect to DARO PLAYOUT at {Host}:{Port}: {Error}", _host, _port, ex.Message);
            await CloseConnectionAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error connecting to DARO PLAYOUT");
            await CloseConnectionAsync().ConfigureAwait(false);
            return false;
        }
    }

    private static void ConfigureKeepalive(Socket socket)
    {
        // Windows-specific keepalive configuration
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            // TCP keepalive configuration for Windows
            // Probe after 30 seconds of idle, then every 5 seconds, up to 3 probes
            // Total detection time: ~45 seconds for dead connection

            // Using IOControl for Windows-specific keepalive configuration
            // Structure: [onoff (4 bytes)] [keepalivetime (4 bytes)] [keepaliveinterval (4 bytes)]
            var keepaliveValues = new byte[12];

            // Enable keepalive
            BitConverter.GetBytes(1).CopyTo(keepaliveValues, 0);

            // Keepalive time: 30000ms (30 seconds) - time before first probe
            BitConverter.GetBytes(30000).CopyTo(keepaliveValues, 4);

            // Keepalive interval: 5000ms (5 seconds) - interval between probes
            BitConverter.GetBytes(5000).CopyTo(keepaliveValues, 8);

            socket.IOControl(IOControlCode.KeepAliveValues, keepaliveValues, null);
        }
        catch (SocketException)
        {
            // Keepalive values configuration is optional and Windows-specific.
            // If it fails (e.g., on non-Windows platforms or restricted environments),
            // the basic SocketOptionName.KeepAlive is still enabled as fallback.
            // This is a static method so we can't log, but the connection will still function.
        }
    }

    private async Task MarkConnectionBrokenAsync()
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _isConnected = false;
            // Don't close the connection here - let EnsureConnectedAsync do it
            // This avoids potential race conditions
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private Task CloseConnectionAsync()
    {
        _isConnected = false;

        // Dispose NetworkStream first (it's owned by TcpClient but we should clean it explicitly)
        try
        {
            _networkStream?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing network stream during cleanup");
        }
        finally
        {
            _networkStream = null;
        }

        // Then dispose TcpClient (this also closes the underlying socket)
        try
        {
            _tcpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing TCP client during cleanup");
        }
        finally
        {
            _tcpClient = null;
        }

        return Task.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(MosartClient));
        }
    }

    public void Dispose()
    {
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
        // Thread-safe check-and-set: only one thread can proceed past this point
        if (Interlocked.CompareExchange(ref _isDisposedInt, 1, 0) != 0) return;

        if (disposing)
        {
            _logger.LogInformation("Disposing MosartClient, closing connection to DARO PLAYOUT");

            // Reuse the existing close logic (nulls out _networkStream/_tcpClient)
            CloseConnectionAsync();

            _connectionLock.Dispose();
            _sendLock.Dispose();
        }
    }
}
