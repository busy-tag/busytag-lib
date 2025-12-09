using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BusyTag.Lib;

/// <summary>
/// JSON converter that handles boolean values that might come as 0/1 integers from PHP APIs
/// </summary>
public class IntToBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Number:
                return reader.GetInt32() != 0;
            case JsonTokenType.String:
                var stringValue = reader.GetString();
                return stringValue == "1" || stringValue?.ToLower() == "true";
            default:
                throw new JsonException($"Cannot convert {reader.TokenType} to boolean");
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}

/// <summary>
/// Client for communicating with BusyTag Cloud Server API
/// </summary>
public class BusyTagCloudClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _deviceId;

    // Shared WebSocket client for real-time command notifications
    private static BusyTagWebSocketClient? _sharedWebSocket;
    private static readonly object _wsLock = new();
    private static readonly Dictionary<string, TaskCompletionSource<CommandUpdatedData>> _pendingCommands = new();

    /// <summary>
    /// Set the shared WebSocket client for real-time command updates
    /// </summary>
    public static void SetSharedWebSocket(BusyTagWebSocketClient? webSocket)
    {
        lock (_wsLock)
        {
            if (_sharedWebSocket != null)
            {
                _sharedWebSocket.CommandUpdated -= OnSharedCommandUpdated;
            }

            _sharedWebSocket = webSocket;

            if (_sharedWebSocket != null)
            {
                _sharedWebSocket.CommandUpdated += OnSharedCommandUpdated;
                System.Diagnostics.Debug.WriteLine("[Cloud] Shared WebSocket client set for command notifications");
            }
        }
    }

    /// <summary>
    /// Whether WebSocket is available for command notifications
    /// </summary>
    public static bool IsWebSocketAvailable => _sharedWebSocket?.IsConnected ?? false;

    private static void OnSharedCommandUpdated(object? sender, CommandUpdatedEventArgs e)
    {
        var data = e.Data;
        System.Diagnostics.Debug.WriteLine($"[Cloud/WS] Command updated: {data.CommandId} -> {data.Status}");

        lock (_wsLock)
        {
            if (_pendingCommands.TryGetValue(data.CommandId, out var tcs))
            {
                tcs.TrySetResult(data);
                _pendingCommands.Remove(data.CommandId);
            }
        }
    }

    public BusyTagCloudClient(string baseUrl, string deviceId)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _deviceId = deviceId;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Device-Key", deviceId);
    }

    /// <summary>
    /// Queue a command for the device to execute
    /// </summary>
    public async Task<CloudCommandResponse> QueueCommandAsync(string command, int priority = 1)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] Queuing command: {command} (priority: {priority})");
            System.Diagnostics.Debug.WriteLine($"[Cloud] URL: {_baseUrl}/device/{_deviceId}/commands");

            var requestBody = new
            {
                command = command,
                priority = priority
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/device/{_deviceId}/commands",
                requestBody
            );

            System.Diagnostics.Debug.WriteLine($"[Cloud] Queue command response: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Cloud] Queue command failed: {errorContent}");

                return new CloudCommandResponse
                {
                    Success = false,
                    ErrorMessage = $"HTTP {response.StatusCode}: {errorContent}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<CloudCommandQueueResult>();
            System.Diagnostics.Debug.WriteLine($"[Cloud] Command queued successfully: {result?.CommandId}");

            return new CloudCommandResponse
            {
                Success = true,
                CommandId = result?.CommandId ?? string.Empty,
                Status = result?.Status ?? "unknown"
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] Queue command exception: {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Cloud] Inner exception: {ex.InnerException?.Message}");

            return new CloudCommandResponse
            {
                Success = false,
                ErrorMessage = $"Failed to queue command: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get the status of a queued command
    /// </summary>
    public async Task<CloudCommandStatus?> GetCommandStatusAsync(string commandId)
    {
        try
        {
            var url = $"{_baseUrl}/commands/{commandId}";
            System.Diagnostics.Debug.WriteLine($"[Cloud] GET {url}");

            var response = await _httpClient.GetAsync(url);

            System.Diagnostics.Debug.WriteLine($"[Cloud] Response: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Cloud] Error: {errorContent}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[Cloud] Response body: {content}");

            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new IntToBoolConverter());

            var status = System.Text.Json.JsonSerializer.Deserialize<CloudCommandStatus>(content, options);

            if (status != null)
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] Parsed: Status={status.Status}, Success={status.Success}, Response={status.Response?.Substring(0, Math.Min(50, status.Response?.Length ?? 0))}...");
            }

            return status;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] GetCommandStatus exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Wait for a command to complete and return its response.
    /// Uses WebSocket for real-time notifications when available, falls back to polling.
    /// </summary>
    public async Task<CloudCommandStatus?> WaitForCommandCompletionAsync(
        string commandId,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        var endTime = DateTime.UtcNow.Add(timeout);

        System.Diagnostics.Debug.WriteLine($"[Cloud] Waiting for command completion: {commandId}");

        // Try WebSocket-based waiting if available
        if (IsWebSocketAvailable)
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] Using WebSocket for command notifications");
            var wsResult = await WaitForCommandViaWebSocketAsync(commandId, timeout);
            if (wsResult != null)
            {
                return wsResult;
            }
            // Fall through to polling if WebSocket didn't get the result
            System.Diagnostics.Debug.WriteLine($"[Cloud] WebSocket wait returned null, falling back to polling");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] WebSocket not available, using polling");
        }

        // Polling fallback
        System.Diagnostics.Debug.WriteLine($"[Cloud] Timeout: {timeout.TotalSeconds}s, Poll interval: {interval.TotalSeconds}s");
        int checkCount = 0;

        while (DateTime.UtcNow < endTime)
        {
            checkCount++;
            var status = await GetCommandStatusAsync(commandId);

            if (status != null)
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] Check #{checkCount}: Status={status.Status}, Success={status.Success}");

                if (status.Status == "completed" || status.Status == "failed")
                {
                    System.Diagnostics.Debug.WriteLine($"[Cloud] Command finished: {status.Status}");
                    return status;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] Check #{checkCount}: No status returned");
            }

            await Task.Delay(interval);
        }

        System.Diagnostics.Debug.WriteLine($"[Cloud] Command timed out after {checkCount} checks");
        return null; // Timeout
    }

    /// <summary>
    /// Wait for command completion via WebSocket notification
    /// </summary>
    private async Task<CloudCommandStatus?> WaitForCommandViaWebSocketAsync(string commandId, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<CommandUpdatedData>();

        lock (_wsLock)
        {
            _pendingCommands[commandId] = tcs;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            cts.Token.Register(() => tcs.TrySetCanceled());

            var result = await tcs.Task;

            System.Diagnostics.Debug.WriteLine($"[Cloud/WS] Received command result: {result.Status}, Success={result.Success}");

            // Convert WebSocket data to CloudCommandStatus
            return new CloudCommandStatus
            {
                CommandId = result.CommandId,
                DeviceId = result.DeviceId,
                Status = result.Status,
                Success = result.Success,
                Response = result.Response,
                CompletedAt = result.CompletedAt
            };
        }
        catch (TaskCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud/WS] WebSocket wait timed out for command {commandId}");
            return null;
        }
        finally
        {
            lock (_wsLock)
            {
                _pendingCommands.Remove(commandId);
            }
        }
    }

    /// <summary>
    /// Check if device is online by querying device status
    /// </summary>
    public async Task<bool> WaitForDeviceOnlineAsync(TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(3);
        var endTime = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < endTime)
        {
            try
            {
                var status = await GetDeviceStatusAsync();

                if (status != null && status.Online)
                {
                    return true;
                }
            }
            catch
            {
                // Continue trying
            }

            await Task.Delay(interval);
        }

        return false;
    }

    /// <summary>
    /// Test cloud connectivity by sending a simple command and waiting for response
    /// </summary>
    public async Task<CloudTestResult> TestCloudConnectionAsync(TimeSpan? timeout = null, bool waitForOnline = true)
    {
        var testTimeout = timeout ?? TimeSpan.FromSeconds(45);

        try
        {
            // First, wait for device to come online if requested
            if (waitForOnline)
            {
                var onlineTimeout = TimeSpan.FromSeconds(Math.Min(30, testTimeout.TotalSeconds / 2));
                var isOnline = await WaitForDeviceOnlineAsync(onlineTimeout);

                if (!isOnline)
                {
                    return new CloudTestResult
                    {
                        Success = false,
                        Message = "Device did not connect to cloud",
                        Details = $"Device not online after {onlineTimeout.TotalSeconds} seconds"
                    };
                }
            }

            // Queue a simple test command (AT+GDN - Get Device Name)
            var queueResult = await QueueCommandAsync("AT+GDN", priority: 10);

            if (!queueResult.Success)
            {
                return new CloudTestResult
                {
                    Success = false,
                    Message = "Failed to queue test command",
                    Details = queueResult.ErrorMessage
                };
            }

            // Wait for the device to pick up and execute the command
            // Use remaining time or at least 30 seconds (device needs time to poll cloud)
            var remainingTimeout = testTimeout - TimeSpan.FromSeconds(waitForOnline ? 30 : 0);
            if (remainingTimeout.TotalSeconds < 30)
                remainingTimeout = TimeSpan.FromSeconds(30);

            System.Diagnostics.Debug.WriteLine($"[Cloud] Waiting up to {remainingTimeout.TotalSeconds}s for device to execute command");

            var commandStatus = await WaitForCommandCompletionAsync(
                queueResult.CommandId,
                remainingTimeout,
                TimeSpan.FromSeconds(3) // Poll every 3 seconds like dashboard (5s) but slightly faster
            );

            if (commandStatus == null)
            {
                return new CloudTestResult
                {
                    Success = false,
                    Message = "Device did not respond within timeout period",
                    Details = $"Command queued but not executed. Device might not be polling for commands.",
                    CommandId = queueResult.CommandId
                };
            }

            if (commandStatus.Status == "completed" && commandStatus.Success == true)
            {
                return new CloudTestResult
                {
                    Success = true,
                    Message = "Cloud connection successful!",
                    Details = $"Device responded: {commandStatus.Response}",
                    CommandId = queueResult.CommandId,
                    Response = commandStatus.Response
                };
            }
            else
            {
                return new CloudTestResult
                {
                    Success = false,
                    Message = "Device responded but command failed",
                    Details = commandStatus.Response ?? "No response",
                    CommandId = queueResult.CommandId
                };
            }
        }
        catch (Exception ex)
        {
            return new CloudTestResult
            {
                Success = false,
                Message = "Cloud connection test failed",
                Details = ex.Message
            };
        }
    }

    /// <summary>
    /// Register device with cloud server
    /// </summary>
    public async Task<bool> RegisterDeviceAsync(string deviceName = "", string firmwareVersion = "")
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] Registering device: {_deviceId}");
            System.Diagnostics.Debug.WriteLine($"[Cloud] URL: {_baseUrl}/device/{_deviceId}/register");
            System.Diagnostics.Debug.WriteLine($"[Cloud] Device Name: {deviceName}, Firmware: {firmwareVersion}");

            var requestBody = new
            {
                device_name = deviceName,
                firmware_version = firmwareVersion
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/device/{_deviceId}/register",
                requestBody
            );

            System.Diagnostics.Debug.WriteLine($"[Cloud] Registration response: {response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Cloud] Registration success: {responseContent}");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Cloud] Registration failed: {errorContent}");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] Registration exception: {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Cloud] Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Check device status on cloud
    /// </summary>
    public async Task<DeviceStatus?> GetDeviceStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/device/{_deviceId}/status");

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var status = await response.Content.ReadFromJsonAsync<DeviceStatus>();

            // Add full image URL if active_image exists
            if (status != null && !string.IsNullOrEmpty(status.ActiveImage))
            {
                var imageUrl = $"{_baseUrl}/uploads/{_deviceId}/{status.ActiveImage}";
                status.ActiveImageUrl = imageUrl;
            }

            return status;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get latest image info for the device (optimized endpoint)
    /// </summary>
    public async Task<LatestImageInfo?> GetLatestImageAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/device/{_deviceId}/image/latest");

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                // No image available (204)
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LatestImageInfo>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Upload an image to the device via cloud
    /// </summary>
    public async Task<CloudImageUploadResponse> UploadImageAsync(byte[] imageData, string fileName)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageData);

            // Set correct MIME type based on file extension
            var extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            var mimeType = extension switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
            content.Add(imageContent, "image", fileName);

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/device/{_deviceId}/image/upload",
                content
            );

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new CloudImageUploadResponse
                {
                    Success = false,
                    ErrorMessage = $"HTTP {response.StatusCode}: {errorContent}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<CloudImageUploadResult>();

            return new CloudImageUploadResponse
            {
                Success = true,
                FileName = result?.Filename ?? fileName,
                Hash = result?.Hash,
                Message = result?.Message
            };
        }
        catch (Exception ex)
        {
            return new CloudImageUploadResponse
            {
                Success = false,
                ErrorMessage = $"Failed to upload image: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Download image from URL
    /// </summary>
    public async Task<byte[]?> DownloadImageAsync(string imageUrl)
    {
        try
        {
            // Convert HTTP to HTTPS if needed
            if (imageUrl.StartsWith("http://"))
            {
                imageUrl = imageUrl.Replace("http://", "https://");
            }

            var response = await _httpClient.GetAsync(imageUrl);
            if (!response.IsSuccessStatusCode)
            {
#if ANDROID
                Android.Util.Log.Debug("BusyTagCloudClient", $"DownloadImageAsync failed: HTTP {response.StatusCode} for URL {imageUrl}");
#endif
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Debug("BusyTagCloudClient", $"DownloadImageAsync exception: {ex.Message} for URL {imageUrl}");
#endif
            return null;
        }
    }

    /// <summary>
    /// Download the active image for the device
    /// </summary>
    public async Task<byte[]?> DownloadActiveImageAsync()
    {
        try
        {
            var imageInfo = await GetLatestImageAsync();
            if (imageInfo == null || string.IsNullOrEmpty(imageInfo.Url))
            {
                return null;
            }

            return await DownloadImageAsync(imageInfo.Url);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set LED solid color via cloud
    /// </summary>
    public async Task<CloudCommandResponse> SetSolidColorAsync(string colorHex, int ledBits = 127, int timeoutSeconds = 30)
    {
        var command = $"AT+SC={ledBits},{colorHex}";
        var queueResult = await QueueCommandAsync(command, priority: 5);

        if (!queueResult.Success || timeoutSeconds <= 0)
            return queueResult;

        // Wait for completion
        var status = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (status != null)
        {
            queueResult.Status = status.Status;
            queueResult.ErrorMessage = status.Response;
        }

        return queueResult;
    }

    /// <summary>
    /// Display an image via cloud
    /// </summary>
    public async Task<CloudCommandResponse> ShowPictureAsync(string fileName, int timeoutSeconds = 30)
    {
        var command = $"AT+SP={fileName}";
        var queueResult = await QueueCommandAsync(command, priority: 5);

        if (!queueResult.Success || timeoutSeconds <= 0)
            return queueResult;

        var status = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (status != null)
        {
            queueResult.Status = status.Status;
            queueResult.ErrorMessage = status.Response;
        }

        return queueResult;
    }

    /// <summary>
    /// Set display brightness via cloud
    /// </summary>
    public async Task<CloudCommandResponse> SetDisplayBrightnessAsync(int brightness, int timeoutSeconds = 30)
    {
        if (brightness < 0 || brightness > 100)
            throw new ArgumentOutOfRangeException(nameof(brightness), "Brightness must be between 0 and 100");

        var command = $"AT+DB={brightness}";
        var queueResult = await QueueCommandAsync(command, priority: 5);

        if (!queueResult.Success || timeoutSeconds <= 0)
            return queueResult;

        var status = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (status != null)
        {
            queueResult.Status = status.Status;
            queueResult.ErrorMessage = status.Response;
        }

        return queueResult;
    }

    /// <summary>
    /// Send LED pattern to device via cloud
    /// </summary>
    /// <param name="patternLines">List of pattern lines to send</param>
    /// <param name="playAfterSending">Whether to automatically play the pattern after sending</param>
    /// <param name="loopCount">Number of times to loop (1-254, or 255 for infinite loop)</param>
    /// <param name="timeoutSeconds">Timeout in seconds for waiting for command completion (0 = don't wait)</param>
    /// <returns>Command response</returns>
    public async Task<CloudCommandResponse> SendPatternAsync(
        List<Util.PatternLine> patternLines,
        bool playAfterSending = true,
        int loopCount = 5,
        int timeoutSeconds = 30)
    {
        if (patternLines == null || patternLines.Count == 0)
        {
            return new CloudCommandResponse
            {
                Success = false,
                ErrorMessage = "Pattern lines cannot be null or empty"
            };
        }

        // Build the AT+CPD command with pattern data
        // Format: AT+CPD=+CP:ledBits,colorHex,speed,delay\r\n+CP:ledBits,colorHex,speed,delay\r\n...
        var patternDataParts = new List<string>();
        foreach (var line in patternLines)
        {
            patternDataParts.Add($"+CP:{line.LedBits},{line.Color},{line.Speed},{line.Delay}");
        }

        var patternData = string.Join("\\r\\n", patternDataParts);
        var command = $"AT+CPD={patternData}";

        System.Diagnostics.Debug.WriteLine($"[Cloud] Sending pattern with {patternLines.Count} lines");
        System.Diagnostics.Debug.WriteLine($"[Cloud] Command: {command}");

        // Queue the pattern command
        var queueResult = await QueueCommandAsync(command, priority: 5);

        if (!queueResult.Success)
            return queueResult;

        // If playAfterSending is true, queue the play command
        if (playAfterSending)
        {
            System.Diagnostics.Debug.WriteLine($"[Cloud] Pattern queued, now queueing play command with loop count: {loopCount}");
            var playCommand = $"AT+PP=1,{loopCount}";
            await QueueCommandAsync(playCommand, priority: 5);
        }

        // If timeout is 0, return immediately without waiting
        if (timeoutSeconds <= 0)
            return queueResult;

        // Wait for the CPD command to complete
        var cpdStatus = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (cpdStatus != null)
        {
            queueResult.Status = cpdStatus.Status;
            queueResult.ErrorMessage = cpdStatus.Response;
        }

        return queueResult;
    }

    /// <summary>
    /// Play or stop LED pattern on device via cloud
    /// </summary>
    /// <param name="play">True to play, false to stop</param>
    /// <param name="loopCount">Number of times to loop (1-254, or 255 for infinite loop)</param>
    /// <param name="timeoutSeconds">Timeout in seconds (0 = don't wait)</param>
    /// <returns>Command response</returns>
    public async Task<CloudCommandResponse> PlayPatternAsync(bool play, int loopCount = 5, int timeoutSeconds = 30)
    {
        var command = $"AT+PP={(play ? 1 : 0)},{loopCount}";
        var queueResult = await QueueCommandAsync(command, priority: 5);

        if (!queueResult.Success || timeoutSeconds <= 0)
            return queueResult;

        var status = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (status != null)
        {
            queueResult.Status = status.Status;
            queueResult.ErrorMessage = status.Response;
        }

        return queueResult;
    }

    /// <summary>
    /// Restart device via cloud
    /// </summary>
    public async Task<CloudCommandResponse> RestartDeviceAsync()
    {
        return await QueueCommandAsync("AT+RST", priority: 10);
    }

    /// <summary>
    /// Get device name via cloud
    /// </summary>
    public async Task<CloudCommandResponse> GetDeviceNameAsync(int timeoutSeconds = 30)
    {
        var queueResult = await QueueCommandAsync("AT+GDN", priority: 5);

        if (!queueResult.Success || timeoutSeconds <= 0)
            return queueResult;

        var status = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (status != null)
        {
            queueResult.Status = status.Status;
            queueResult.ErrorMessage = status.Response;
        }

        return queueResult;
    }

    /// <summary>
    /// Get firmware version via cloud
    /// </summary>
    public async Task<CloudCommandResponse> GetFirmwareVersionAsync(int timeoutSeconds = 30)
    {
        var queueResult = await QueueCommandAsync("AT+GFV", priority: 5);

        if (!queueResult.Success || timeoutSeconds <= 0)
            return queueResult;

        var status = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (status != null)
        {
            queueResult.Status = status.Status;
            queueResult.ErrorMessage = status.Response;
        }

        return queueResult;
    }

    /// <summary>
    /// Send a custom AT command via cloud
    /// </summary>
    public async Task<CloudCommandResponse> SendCustomCommandAsync(string command, int priority = 5, int timeoutSeconds = 30)
    {
        var queueResult = await QueueCommandAsync(command, priority);

        if (!queueResult.Success || timeoutSeconds <= 0)
            return queueResult;

        var status = await WaitForCommandCompletionAsync(
            queueResult.CommandId,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1)
        );

        if (status != null)
        {
            queueResult.Status = status.Status;
            queueResult.ErrorMessage = status.Response;
        }

        return queueResult;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

// Response models
public class CloudCommandResponse
{
    public bool Success { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public class CloudCommandQueueResult
{
    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

public class CloudCommandStatus
{
    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("success")]
    [JsonConverter(typeof(IntToBoolConverter))]
    public bool? Success { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("sent_at")]
    public string? SentAt { get; set; }

    [JsonPropertyName("completed_at")]
    public string? CompletedAt { get; set; }
}

public class CloudTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? CommandId { get; set; }
    public string? Response { get; set; }
}

public class DeviceStatus
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("firmware_version")]
    public string? FirmwareVersion { get; set; }

    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; set; }

    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("online")]
    [JsonConverter(typeof(IntToBoolConverter))]
    public bool Online { get; set; }

    [JsonPropertyName("wifi_connected")]
    [JsonConverter(typeof(IntToBoolConverter))]
    public bool? WifiConnected { get; set; }  // Made nullable since API doesn't return it

    [JsonPropertyName("storage_mounted")]
    [JsonConverter(typeof(IntToBoolConverter))]
    public bool? StorageMounted { get; set; }

    [JsonPropertyName("storage_total")]
    public long? StorageTotal { get; set; }

    [JsonPropertyName("storage_free")]
    public long? StorageFree { get; set; }

    [JsonPropertyName("brightness")]
    public int? Brightness { get; set; }

    [JsonPropertyName("current_image")]
    public string? CurrentImage { get; set; }

    [JsonPropertyName("image_count")]
    public int? ImageCount { get; set; }

    [JsonPropertyName("active_image")]
    public string? ActiveImage { get; set; }

    [JsonIgnore]
    public string? ActiveImageUrl { get; set; }

    [JsonPropertyName("pending_commands")]
    public int? PendingCommands { get; set; }

    [JsonPropertyName("completed_commands")]
    public int? CompletedCommands { get; set; }

    [JsonPropertyName("event_count")]
    public int? EventCount { get; set; }
}

public class LatestImageInfo
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }
}

public class CloudImageUploadResponse
{
    public bool Success { get; set; }
    public string? FileName { get; set; }
    public string? Hash { get; set; }
    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }
}

public class CloudImageUploadResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
