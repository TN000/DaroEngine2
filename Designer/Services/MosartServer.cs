using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DaroDesigner.Services
{
    /// <summary>
    /// Mosart command types for broadcast automation integration.
    /// </summary>
    public enum MosartCommand
    {
        /// <summary>CUE - Load item from database and add to playlist.</summary>
        Cue = 0,
        /// <summary>PLAY - Execute take (play current cued item).</summary>
        Play = 1,
        /// <summary>STOP - Clear on-air item and remove from playlist.</summary>
        Stop = 2,
        /// <summary>CONTINUE - Resume from pause (same as Play).</summary>
        Continue = 3,
        /// <summary>PAUSE - Pause playback.</summary>
        Pause = 4
    }

    /// <summary>
    /// Parsed Mosart protocol message.
    /// Format: GUID|COMMAND\r\n
    /// </summary>
    public sealed class MosartMessage
    {
        public string ItemId { get; }
        public MosartCommand Command { get; }

        public MosartMessage(string itemId, MosartCommand command)
        {
            ItemId = itemId;
            Command = command;
        }

        public static bool TryParse(string rawMessage, out MosartMessage message)
        {
            message = null;

            if (string.IsNullOrWhiteSpace(rawMessage))
                return false;

            var parts = rawMessage.Trim().Split('|');
            if (parts.Length < 2)
                return false;

            var itemId = parts[0].Trim();
            if (!Guid.TryParse(itemId, out _))
                return false;

            if (!int.TryParse(parts[1].Trim(), out var commandValue))
                return false;

            if (!Enum.IsDefined(typeof(MosartCommand), commandValue))
                return false;

            message = new MosartMessage(itemId, (MosartCommand)commandValue);
            return true;
        }
    }

    /// <summary>
    /// Event args for Mosart command received.
    /// </summary>
    public class MosartCommandEventArgs : EventArgs
    {
        public MosartMessage Message { get; }
        public Action<string> SendResponse { get; }

        public MosartCommandEventArgs(MosartMessage message, Action<string> sendResponse)
        {
            Message = message;
            SendResponse = sendResponse;
        }
    }

    /// <summary>
    /// High-performance TCP server for Mosart automation commands.
    /// Uses System.IO.Pipelines for efficient buffer management.
    /// Protocol: GUID|COMMAND\r\n
    /// </summary>
    public sealed class MosartServer : IDisposable
    {
        private readonly int _port;
        private readonly object _lockObj = new object();
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private volatile bool _isRunning;  // volatile for thread-safe Start/Stop coordination
        private volatile bool _disposed;   // volatile for thread-safe disposal check

        // Security: Connection and rate limits (use centralized constants)
        private static int MaxConnections => AppConstants.MaxConnectionsPerIp;
        private static int MaxCommandsPerSecond => AppConstants.RateLimitPerSecond;
        private static int ConnectionTimeoutMs => AppConstants.MosartConnectionTimeoutMs;
        private static int ReadTimeoutMs => AppConstants.MosartReadTimeoutMs;
        private readonly Dictionary<string, RateLimitInfo> _rateLimits = new Dictionary<string, RateLimitInfo>();
        private readonly object _rateLimitLock = new object();

        private class RateLimitInfo
        {
            public int CommandCount;
            public DateTime WindowStart;
        }

        /// <summary>
        /// Event raised when a command is received from Mosart.
        /// Handler runs on thread pool - use Dispatcher to update UI.
        /// </summary>
        public event EventHandler<MosartCommandEventArgs> CommandReceived;

        /// <summary>
        /// Event raised when server status changes.
        /// </summary>
        public event EventHandler<string> StatusChanged;

        /// <summary>
        /// Event raised for logging purposes.
        /// </summary>
        public event EventHandler<string> LogMessage;

        public bool IsRunning => _isRunning;
        public int Port => _port;
        public int ClientCount
        {
            get { lock (_lockObj) { return _clients.Count; } }
        }

        public MosartServer(int port = 5555)
        {
            _port = port;
        }

        /// <summary>
        /// Starts the TCP server.
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _isRunning = true;

                _acceptTask = Task.Run(() => AcceptClientsAsync(_cts.Token));

                Log($"Mosart server started on port {_port}");
                StatusChanged?.Invoke(this, $"Listening on port {_port}");
            }
            catch (Exception ex)
            {
                Log($"Failed to start server: {ex.Message}");
                StatusChanged?.Invoke(this, $"Error: {ex.Message}");
                _isRunning = false;
            }
        }

        /// <summary>
        /// Stops the TCP server and disconnects all clients.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
                // TcpListener should be disposed to release the port binding
                (_listener as IDisposable)?.Dispose();
                _listener = null;
            }
            catch (Exception ex)
            {
                Log($"Error stopping listener: {ex.Message}");
            }

            lock (_lockObj)
            {
                foreach (var client in _clients)
                {
                    try
                    {
                        // TcpClient.Dispose() closes and releases socket resources
                        client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log($"Error disposing client: {ex.Message}");
                    }
                }
                _clients.Clear();
            }

            Log("Mosart server stopped");
            StatusChanged?.Invoke(this, "Stopped");
        }

        /// <summary>
        /// Broadcasts a message to all connected clients.
        /// </summary>
        public async Task BroadcastAsync(string message)
        {
            TcpClient[] clients;
            lock (_lockObj)
            {
                clients = _clients.ToArray();
            }

            var bytes = Encoding.UTF8.GetBytes(message + "\r\n");

            foreach (var client in clients)
            {
                try
                {
                    if (client.Connected)
                    {
                        var stream = client.GetStream();
                        // Synchronize with SendResponse which may write to the same stream
                        lock (stream)
                        {
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush();
                        }
                    }
                }
                catch (Exception)
                {
                    // Client disconnected, will be cleaned up
                }
            }
        }

        /// <summary>
        /// Broadcasts state change notification.
        /// </summary>
        public async void BroadcastState(string state)
        {
            try
            {
                await BroadcastAsync($"STATE:{state}");
            }
            catch (Exception ex)
            {
                Log($"BroadcastState error: {ex.Message}");
            }
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

                    // Security: Check connection limit
                    bool accepted;
                    lock (_lockObj)
                    {
                        if (_clients.Count >= MaxConnections)
                        {
                            Log($"Connection rejected (max {MaxConnections} reached): {endpoint}");
                            try { client.Dispose(); }
                            catch (Exception ex) { Log($"Error disposing rejected client: {ex.Message}"); }
                            continue;
                        }
                        _clients.Add(client);
                        accepted = true;
                    }

                    if (accepted)
                    {
                        Log($"Client connected: {endpoint}");
                        StatusChanged?.Invoke(this, $"Connected: {ClientCount} client(s)");

                        // Handle client in background
                        _ = HandleClientAsync(client, ct);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Log($"Accept error: {ex.Message}");
                        await Task.Delay(100, ct);
                    }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

            try
            {
                client.NoDelay = true;
                // Security: Set socket timeouts to prevent Slowloris attacks
                client.ReceiveTimeout = ConnectionTimeoutMs;
                client.SendTimeout = 5000;

                using (var stream = client.GetStream())
                {
                    // Set stream-level read timeout for additional protection
                    stream.ReadTimeout = ConnectionTimeoutMs;

                    var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
                        bufferSize: AppConstants.MaxMessageLength,
                        minimumReadSize: 64));

                    DateTime lastActivity = DateTime.UtcNow;

                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        ReadResult result;
                        try
                        {
                            // Create a timeout for individual read operations to prevent slow reads
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            readCts.CancelAfter(ReadTimeoutMs);

                            result = await reader.ReadAsync(readCts.Token);
                            lastActivity = DateTime.UtcNow;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Read timeout - check if we have incomplete data (Slowloris indicator)
                            Log($"Read timeout for {endpoint} - potential slow read attack");
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (IOException)
                        {
                            break;
                        }

                        var buffer = result.Buffer;

                        while (TryReadLine(ref buffer, out var line))
                        {
                            var message = Encoding.UTF8.GetString(line.ToArray());
                            ProcessMessage(message, stream, endpoint);
                        }

                        reader.AdvanceTo(buffer.Start, buffer.End);

                        if (result.IsCompleted)
                            break;

                        // Check for idle timeout (no complete messages received)
                        if ((DateTime.UtcNow - lastActivity).TotalMilliseconds > ConnectionTimeoutMs)
                        {
                            Log($"Idle timeout for {endpoint}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Client error ({endpoint}): {ex.Message}");
            }
            finally
            {
                lock (_lockObj)
                {
                    _clients.Remove(client);
                }

                try { client.Dispose(); }
                catch (Exception ex) { Log($"Error disposing client {endpoint}: {ex.Message}"); }

                Log($"Client disconnected: {endpoint}");
                StatusChanged?.Invoke(this, ClientCount > 0
                    ? $"Connected: {ClientCount} client(s)"
                    : $"Listening on port {_port}");
            }
        }

        private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
        {
            var reader = new SequenceReader<byte>(buffer);

            // Look for \r\n first
            if (reader.TryReadTo(out line, new byte[] { 0x0D, 0x0A }, advancePastDelimiter: true))
            {
                buffer = buffer.Slice(reader.Position);
                return true;
            }

            // Try just \n
            reader = new SequenceReader<byte>(buffer);
            if (reader.TryReadTo(out line, (byte)'\n', advancePastDelimiter: true))
            {
                buffer = buffer.Slice(reader.Position);
                return true;
            }

            line = default;
            return false;
        }

        private void ProcessMessage(string rawMessage, NetworkStream stream, string clientEndpoint)
        {
            Log($"Received: {rawMessage}");

            // Security: Rate limiting
            if (!CheckRateLimit(clientEndpoint))
            {
                Log($"Rate limit exceeded for {clientEndpoint}");
                SendResponse(stream, "ERROR:RATE_LIMIT_EXCEEDED");
                return;
            }

            // Security: Input length validation
            if (rawMessage.Length > AppConstants.MaxMessageLength)
            {
                Log($"Message too long from {clientEndpoint}: {rawMessage.Length} bytes (max {AppConstants.MaxMessageLength})");
                SendResponse(stream, "ERROR:MESSAGE_TOO_LONG");
                return;
            }

            if (!MosartMessage.TryParse(rawMessage, out var message))
            {
                Log($"Invalid message format: {rawMessage}");
                SendResponse(stream, "ERROR:INVALID_FORMAT");
                return;
            }

            // Create response callback
            Action<string> sendResponse = (response) => SendResponse(stream, response);

            // Raise event for command handling
            CommandReceived?.Invoke(this, new MosartCommandEventArgs(message, sendResponse));
        }

        private bool CheckRateLimit(string endpoint)
        {
            var now = DateTime.UtcNow;
            var clientIp = endpoint.Split(':')[0]; // Extract IP from endpoint

            lock (_rateLimitLock)
            {
                if (!_rateLimits.TryGetValue(clientIp, out var info))
                {
                    info = new RateLimitInfo { CommandCount = 0, WindowStart = now };
                    _rateLimits[clientIp] = info;
                }

                // Reset window if expired
                if ((now - info.WindowStart).TotalSeconds >= 1.0)
                {
                    info.CommandCount = 0;
                    info.WindowStart = now;
                }

                info.CommandCount++;
                return info.CommandCount <= MaxCommandsPerSecond;
            }
        }

        private void SendResponse(NetworkStream stream, string response)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(response + "\r\n");
                // Synchronize with BroadcastAsync which may write to the same stream
                lock (stream)
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                Log($"Sent: {response}");
            }
            catch (Exception ex)
            {
                Log($"Send error: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            LogMessage?.Invoke(this, $"[Mosart] {message}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            // Wait for accept task to complete (with timeout)
            try
            {
                _acceptTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException) { }
            catch (TaskCanceledException) { }

            _cts?.Dispose();
            _cts = null;

            // Clear rate limit dictionary
            lock (_rateLimitLock)
            {
                _rateLimits.Clear();
            }

            Log("Mosart server disposed");
        }
    }
}
