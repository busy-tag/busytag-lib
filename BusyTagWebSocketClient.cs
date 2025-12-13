using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SocketIOClient;

namespace BusyTag.Lib;

/// <summary>
/// WebSocket client for real-time updates from BusyTag Cloud Server.
/// Note: WebSocket is disabled on Windows and macOS since device communication uses USB on those platforms.
/// </summary>
public class BusyTagWebSocketClient : IDisposable
{
    private SocketIOClient.SocketIO? _socket;
    private readonly string _serverUrl;
    private readonly string? _socketPath;
    private bool _isConnected;
    private bool _isDisposed;
    private readonly HashSet<string> _subscribedDevices = new();

    /// <summary>
    /// Returns true if WebSocket is supported on the current platform.
    /// WebSocket is disabled on Windows and macOS since device communication uses USB on those platforms.
    /// </summary>
    public static bool IsPlatformSupported
    {
        get
        {
            // Disable WebSocket on Windows and macOS - USB is used for device communication
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return false;

#if MACCATALYST || __MACCATALYST__
            return false;
#else
            return true;
#endif
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new IntToBoolConverter() }
    };

    /// <summary>
    /// Fired when connection state changes
    /// </summary>
    public event EventHandler<WebSocketConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>
    /// Fired when a device status update is received
    /// </summary>
    public event EventHandler<DeviceUpdateEventArgs>? DeviceUpdated;

    /// <summary>
    /// Fired when a new device is registered
    /// </summary>
    public event EventHandler<DeviceRegisteredEventArgs>? DeviceRegistered;

    /// <summary>
    /// Fired when a command is created
    /// </summary>
    public event EventHandler<CommandCreatedEventArgs>? CommandCreated;

    /// <summary>
    /// Fired when a command status is updated
    /// </summary>
    public event EventHandler<CommandUpdatedEventArgs>? CommandUpdated;

    /// <summary>
    /// Fired when a device event is created
    /// </summary>
    public event EventHandler<DeviceEventCreatedEventArgs>? DeviceEventCreated;

    /// <summary>
    /// Fired when an image is uploaded
    /// </summary>
    public event EventHandler<ImageUploadedEventArgs>? ImageUploaded;

    /// <summary>
    /// Whether the WebSocket is currently connected.
    /// Always returns false on Windows and macOS since WebSocket is disabled on those platforms.
    /// </summary>
    public bool IsConnected => IsPlatformSupported && _isConnected;

    /// <summary>
    /// Creates a new WebSocket client for BusyTag Cloud
    /// </summary>
    /// <param name="serverUrl">WebSocket server URL (e.g., https://greynut.com)</param>
    /// <param name="socketPath">Optional Socket.IO path (default: /socket.io)</param>
    public BusyTagWebSocketClient(string serverUrl, string? socketPath = null)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _socketPath = socketPath;
    }

    /// <summary>
    /// Connect to the WebSocket server.
    /// On Windows and macOS, this method returns immediately without connecting since USB is used for device communication.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(BusyTagWebSocketClient));

        // Skip WebSocket connection on Windows and macOS - USB is used for device communication
        if (!IsPlatformSupported)
        {
            System.Diagnostics.Debug.WriteLine("[WebSocket] Platform not supported (Windows/macOS use USB) - skipping connection");
            return;
        }

        if (_socket != null && _isConnected) return;

        try
        {
            var options = new SocketIOOptions
            {
                Transport = SocketIOClient.Transport.TransportProtocol.WebSocket,
                Reconnection = true,
                ReconnectionAttempts = int.MaxValue, // Keep trying indefinitely
                ReconnectionDelay = 2000,
                ReconnectionDelayMax = 30000, // Max 30 seconds between retries
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                // Set the Origin header for proxy compatibility (Go proxies require valid Origin)
                ExtraHeaders = new Dictionary<string, string>
                {
                    { "Origin", "https://greynut.com" }
                }
            };

            if (!string.IsNullOrEmpty(_socketPath))
            {
                options.Path = _socketPath;
            }

            _socket = new SocketIOClient.SocketIO(_serverUrl, options);

            // Connection events
            _socket.OnConnected += OnConnected;
            _socket.OnDisconnected += OnDisconnected;
            _socket.OnError += OnError;
            _socket.OnReconnectAttempt += OnReconnectAttempt;
            _socket.OnReconnected += OnReconnected;
            _socket.OnReconnectError += OnReconnectError;
            _socket.OnReconnectFailed += OnReconnectFailed;

            // Device events
            _socket.On("device:update", response => OnDeviceUpdate(response));
            _socket.On("device:registered", response => OnDeviceRegistered(response));

            // Command events
            _socket.On("command:created", response => OnCommandCreated(response));
            _socket.On("command:updated", response => OnCommandUpdated(response));

            // Other events
            _socket.On("event:created", response => OnDeviceEventCreated(response));
            _socket.On("image:uploaded", response => OnImageUploaded(response));

            System.Diagnostics.Debug.WriteLine($"[WebSocket] Connecting to {_serverUrl} with path {_socketPath}...");
            await _socket.ConnectAsync();
            System.Diagnostics.Debug.WriteLine($"[WebSocket] ConnectAsync completed, IsConnected={_isConnected}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Connection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Disconnect from the WebSocket server
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_socket == null) return;

        try
        {
            await _socket.DisconnectAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Disconnect error: {ex.Message}");
        }
    }

    /// <summary>
    /// Subscribe to updates for a specific device
    /// </summary>
    public async Task SubscribeToDeviceAsync(string deviceId)
    {
        if (_socket == null || !_isConnected) return;

        if (_subscribedDevices.Contains(deviceId)) return;

        try
        {
            await _socket.EmitAsync("subscribe:device", deviceId);
            _subscribedDevices.Add(deviceId);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Subscribed to device: {deviceId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Subscribe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Unsubscribe from updates for a specific device
    /// </summary>
    public async Task UnsubscribeFromDeviceAsync(string deviceId)
    {
        if (_socket == null || !_isConnected) return;

        try
        {
            await _socket.EmitAsync("unsubscribe:device", deviceId);
            _subscribedDevices.Remove(deviceId);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Unsubscribed from device: {deviceId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Unsubscribe failed: {ex.Message}");
        }
    }

    #region Event Handlers

    private void OnConnected(object? sender, EventArgs e)
    {
        _isConnected = true;
        System.Diagnostics.Debug.WriteLine("[WebSocket] Connected");

        // Re-subscribe to all previously subscribed devices
        foreach (var deviceId in _subscribedDevices.ToList())
        {
            _ = _socket?.EmitAsync("subscribe:device", deviceId);
        }

        ConnectionChanged?.Invoke(this, new WebSocketConnectionChangedEventArgs(true, null));
    }

    private void OnDisconnected(object? sender, string reason)
    {
        _isConnected = false;
        System.Diagnostics.Debug.WriteLine($"[WebSocket] Disconnected: {reason}");
        ConnectionChanged?.Invoke(this, new WebSocketConnectionChangedEventArgs(false, reason));
    }

    private void OnError(object? sender, string error)
    {
        System.Diagnostics.Debug.WriteLine($"[WebSocket] Error: {error}");
        // Trigger reconnection on error
        if (_socket != null && !_isConnected)
        {
            System.Diagnostics.Debug.WriteLine("[WebSocket] Will attempt reconnection...");
        }
    }

    private void OnReconnectAttempt(object? sender, int attempt)
    {
        System.Diagnostics.Debug.WriteLine($"[WebSocket] Reconnection attempt {attempt}");
    }

    private void OnReconnected(object? sender, int attempt)
    {
        System.Diagnostics.Debug.WriteLine($"[WebSocket] Reconnected after {attempt} attempts");
        _isConnected = true;

        // Re-subscribe to all previously subscribed devices
        foreach (var deviceId in _subscribedDevices.ToList())
        {
            _ = _socket?.EmitAsync("subscribe:device", deviceId);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Re-subscribed to device: {deviceId}");
        }

        ConnectionChanged?.Invoke(this, new WebSocketConnectionChangedEventArgs(true, null));
    }

    private void OnReconnectError(object? sender, Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[WebSocket] Reconnection error: {ex.Message}");
    }

    private void OnReconnectFailed(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[WebSocket] Reconnection failed - all attempts exhausted");
        // Optionally, try to manually reconnect after a delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!_isConnected && !_isDisposed)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocket] Attempting manual reconnection...");
                try
                {
                    await _socket?.ConnectAsync()!;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebSocket] Manual reconnection failed: {ex.Message}");
                }
            }
        });
    }

    private void OnDeviceUpdate(SocketIOResponse response)
    {
        try
        {
            var rawJson = response.GetValue<JsonElement>().GetRawText();
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Device update raw: {rawJson}");

            var data = JsonSerializer.Deserialize<DeviceUpdateData>(rawJson, JsonOptions);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Device update parsed: DeviceId={data?.DeviceId}, Online={data?.Online}");
            if (data != null && !string.IsNullOrEmpty(data.DeviceId))
            {
                DeviceUpdated?.Invoke(this, new DeviceUpdateEventArgs(data));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Failed to parse device update: {ex.Message}");
        }
    }

    private void OnDeviceRegistered(SocketIOResponse response)
    {
        try
        {
            var rawJson = response.GetValue<JsonElement>().GetRawText();
            var data = JsonSerializer.Deserialize<DeviceRegisteredData>(rawJson, JsonOptions);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Device registered: {data?.DeviceId}");
            if (data != null && !string.IsNullOrEmpty(data.DeviceId))
            {
                DeviceRegistered?.Invoke(this, new DeviceRegisteredEventArgs(data));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Failed to parse device registered: {ex.Message}");
        }
    }

    private void OnCommandCreated(SocketIOResponse response)
    {
        try
        {
            var rawJson = response.GetValue<JsonElement>().GetRawText();
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Command created raw: {rawJson}");

            var data = JsonSerializer.Deserialize<CommandCreatedData>(rawJson, JsonOptions);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Command created parsed: CommandId={data?.CommandId}, Status={data?.Status}");
            if (data != null && !string.IsNullOrEmpty(data.CommandId))
            {
                CommandCreated?.Invoke(this, new CommandCreatedEventArgs(data));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Failed to parse command created: {ex.Message}");
        }
    }

    private void OnCommandUpdated(SocketIOResponse response)
    {
        try
        {
            var rawJson = response.GetValue<JsonElement>().GetRawText();
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Command updated raw: {rawJson}");

            var data = JsonSerializer.Deserialize<CommandUpdatedData>(rawJson, JsonOptions);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Command updated parsed: CommandId={data?.CommandId}, Status={data?.Status}, Success={data?.Success}");
            if (data != null && !string.IsNullOrEmpty(data.CommandId))
            {
                CommandUpdated?.Invoke(this, new CommandUpdatedEventArgs(data));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Failed to parse command updated: {ex.Message}");
        }
    }

    private void OnDeviceEventCreated(SocketIOResponse response)
    {
        try
        {
            var rawJson = response.GetValue<JsonElement>().GetRawText();
            var data = JsonSerializer.Deserialize<DeviceEventData>(rawJson, JsonOptions);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Device event: DeviceId={data?.DeviceId}, Type={data?.EventType}, Data={data?.EventData}");
            if (data != null && !string.IsNullOrEmpty(data.DeviceId))
            {
                DeviceEventCreated?.Invoke(this, new DeviceEventCreatedEventArgs(data));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Failed to parse device event: {ex.Message}");
        }
    }

    private void OnImageUploaded(SocketIOResponse response)
    {
        try
        {
            var rawJson = response.GetValue<JsonElement>().GetRawText();
            var data = JsonSerializer.Deserialize<ImageUploadedData>(rawJson, JsonOptions);
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Image uploaded: DeviceId={data?.DeviceId}, Filename={data?.Filename}");
            if (data != null && !string.IsNullOrEmpty(data.DeviceId))
            {
                ImageUploaded?.Invoke(this, new ImageUploadedEventArgs(data));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebSocket] Failed to parse image uploaded: {ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _socket?.Dispose();
        _socket = null;
    }
}

#region Event Args and Data Classes

public class WebSocketConnectionChangedEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string? Reason { get; }

    public WebSocketConnectionChangedEventArgs(bool isConnected, string? reason)
    {
        IsConnected = isConnected;
        Reason = reason;
    }
}

public class DeviceUpdateEventArgs : EventArgs
{
    public DeviceUpdateData Data { get; }
    public DeviceUpdateEventArgs(DeviceUpdateData data) => Data = data;
}

public class DeviceRegisteredEventArgs : EventArgs
{
    public DeviceRegisteredData Data { get; }
    public DeviceRegisteredEventArgs(DeviceRegisteredData data) => Data = data;
}

public class CommandCreatedEventArgs : EventArgs
{
    public CommandCreatedData Data { get; }
    public CommandCreatedEventArgs(CommandCreatedData data) => Data = data;
}

public class CommandUpdatedEventArgs : EventArgs
{
    public CommandUpdatedData Data { get; }
    public CommandUpdatedEventArgs(CommandUpdatedData data) => Data = data;
}

public class DeviceEventCreatedEventArgs : EventArgs
{
    public DeviceEventData Data { get; }
    public DeviceEventCreatedEventArgs(DeviceEventData data) => Data = data;
}

public class ImageUploadedEventArgs : EventArgs
{
    public ImageUploadedData Data { get; }
    public ImageUploadedEventArgs(ImageUploadedData data) => Data = data;
}

// Data classes for WebSocket events (snake_case JSON from server)
public class DeviceUpdateData
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("online")]
    public bool Online { get; set; }

    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; set; }

    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("wifi_connected")]
    public bool WifiConnected { get; set; }

    [JsonPropertyName("storage_mounted")]
    public bool StorageMounted { get; set; }

    [JsonPropertyName("storage_total")]
    public long StorageTotal { get; set; }

    [JsonPropertyName("storage_free")]
    public long StorageFree { get; set; }

    [JsonPropertyName("brightness")]
    public int Brightness { get; set; }

    [JsonPropertyName("current_image")]
    public string? CurrentImage { get; set; }
}

public class DeviceRegisteredData
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("firmware_version")]
    public string? FirmwareVersion { get; set; }

    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("registered_at")]
    public string? RegisteredAt { get; set; }
}

public class CommandCreatedData
{
    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}

public class CommandUpdatedData
{
    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("completed_at")]
    public string? CompletedAt { get; set; }
}

public class DeviceEventData
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("event_data")]
    public string? EventData { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public class ImageUploadedData
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("device_filename")]
    public string? DeviceFilename { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("uploaded_at")]
    public string? UploadedAt { get; set; }
}

#endregion
