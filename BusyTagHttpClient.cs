using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace BusyTag.Lib;

public class BusyTagHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private string? _authToken;

    public BusyTagHttpClient(string deviceHost = "192.168.4.1", int port = 80, string? authToken = null)
    {
        _baseUrl = $"http://{deviceHost}:{port}";
        _authToken = authToken;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(10) // Shorter timeout for faster fallback
        };

        Debug.WriteLine($"BusyTagHttpClient initialized with base URL: {_baseUrl}");
    }

    /// <summary>
    /// Fetch the homepage and extract the auth token
    /// </summary>
    public async Task<string?> FetchAuthTokenAsync()
    {
        try
        {
            Debug.WriteLine($"[HTTP] Fetching auth token from {_baseUrl}/");

            var response = await _httpClient.GetAsync("/");

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[HTTP] Failed to fetch homepage: {response.StatusCode}");
                return null;
            }

            var html = await response.Content.ReadAsStringAsync();

            // Parse the token from the HTML (look for "Bearer TOKEN")
            // Example: -H "Authorization: Bearer <span class='token'>abc123def456</span>"
            var match = System.Text.RegularExpressions.Regex.Match(
                html,
                @"Bearer\s+<span class='token'>([^<]+)</span>"
            );

            if (match.Success)
            {
                var token = match.Groups[1].Value;
                Debug.WriteLine($"[HTTP] Extracted auth token: {token}");

                // Update the auth token
                _authToken = token;

                return token;
            }

            Debug.WriteLine($"[HTTP] Could not find auth token in homepage");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HTTP] Exception fetching auth token: {ex.Message}");
            return null;
        }
    }

    public async Task<AtCommandResponse> SendAtCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        try
        {
            Debug.WriteLine($"[HTTP] Sending AT command: {command}");

            var request = new HttpRequestMessage(HttpMethod.Post, "/at");

            // Add authorization header if token is provided
            if (!string.IsNullOrEmpty(_authToken))
            {
                request.Headers.Add("Authorization", $"Bearer {_authToken}");
            }

            // Create JSON body
            var commandData = new { command = command };
            var jsonContent = JsonSerializer.Serialize(commandData);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            Debug.WriteLine($"[HTTP] Request URL: {_baseUrl}/at");
            Debug.WriteLine($"[HTTP] Request body: {jsonContent}");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Debug.WriteLine($"[HTTP] Response status: {response.StatusCode}");
            Debug.WriteLine($"[HTTP] Response body: {responseContent}");

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[HTTP] Error: {response.StatusCode} - {responseContent}");
                return new AtCommandResponse
                {
                    Status = "error",
                    Message = $"HTTP {response.StatusCode}: {responseContent}",
                    Data = null
                };
            }

            var result = JsonSerializer.Deserialize<AtCommandResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Debug.WriteLine($"[HTTP] Parsed response - Status: {result?.Status}, Message: {result?.Message}, Data: {result?.Data}");

            return result ?? new AtCommandResponse { Status = "error", Message = "Failed to parse response", Data = null };
        }
        catch (TaskCanceledException ex)
        {
            Debug.WriteLine($"[HTTP] Request timeout: {ex.Message}");
            return new AtCommandResponse
            {
                Status = "error",
                Message = "Request timeout - device did not respond",
                Data = null
            };
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[HTTP] Connection error: {ex.Message}");
            return new AtCommandResponse
            {
                Status = "error",
                Message = $"Cannot connect to device: {ex.Message}",
                Data = null
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HTTP] Unexpected error: {ex.Message}");
            Debug.WriteLine($"[HTTP] Stack trace: {ex.StackTrace}");
            return new AtCommandResponse
            {
                Status = "error",
                Message = $"Error: {ex.Message}",
                Data = null
            };
        }
    }

    public async Task<bool> SetWifiStationCredentialsAsync(string ssid, string password)
    {
        var response = await SendAtCommandAsync($"AT+STA={ssid},{password}");
        return response.IsSuccess && (response.Data?.Contains("OK") ?? false);
    }

    public async Task<bool> SetWifiModeAsync(int mode)
    {
        if (mode is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(mode), "WiFi mode must be between 0 and 3");

        var response = await SendAtCommandAsync($"AT+WM={mode}");
        return response.IsSuccess && (response.Data?.Contains("OK") ?? false);
    }

    public async Task<AtCommandResponse> GetDeviceInfoAsync()
    {
        return await SendAtCommandAsync("AT+GDN");
    }

    public async Task<AtCommandResponse> GetStationStatusAsync()
    {
        return await SendAtCommandAsync("AT+STA?");
    }

    public async Task<AtCommandResponse> GetWifiModeAsync()
    {
        return await SendAtCommandAsync("AT+WM?");
    }

    public async Task<AtCommandResponse> GetHardwareVersionAsync()
    {
        return await SendAtCommandAsync("AT+GHV");
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var response = await SendAtCommandAsync("AT");
            return response.IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start a Wi-Fi network scan on the device.
    /// The scan runs asynchronously on the device.
    /// </summary>
    public async Task<bool> StartWifiScanAsync()
    {
        var response = await SendAtCommandAsync("AT+WSCAN");
        return response.IsSuccess && (response.Data?.Contains("OK") ?? false);
    }

    /// <summary>
    /// Get Wi-Fi scan results from the device.
    /// Returns a list of available networks with SSID, channel, RSSI, and auth mode.
    /// </summary>
    public async Task<WifiScanResult> GetWifiScanResultsAsync()
    {
        var response = await SendAtCommandAsync("AT+WSCAN?");

        var result = new WifiScanResult();

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Data))
        {
            result.ErrorMessage = response.Message ?? "Failed to get scan results";
            return result;
        }

        var data = response.Data.Trim();

        // Check if the scan is still in progress
        if (data.Contains("+WSCAN:BUSY"))
        {
            result.IsBusy = true;
            return result;
        }

        // Parse results: +WSCAN:<count> followed by +WSCAN:<ssid>,<channel>,<rssi>,<auth>
        var lines = data.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("+WSCAN:"))
            {
                var content = line.Substring(7); // Remove "+WSCAN:"

                // Check if this is the count line (just a number)
                if (int.TryParse(content, out var count))
                {
                    result.TotalCount = count;
                    continue;
                }

                // Parse network entry: <ssid>,<channel>,<rssi>,<auth>
                var parts = content.Split(',');
                if (parts.Length >= 4)
                {
                    var network = new WifiNetwork
                    {
                        Ssid = parts[0],
                        Channel = int.TryParse(parts[1], out var ch) ? ch : 0,
                        Rssi = int.TryParse(parts[2], out var rssi) ? rssi : -100,
                        AuthMode = parts[3]
                    };

                    // Only add non-empty SSIDs
                    if (!string.IsNullOrWhiteSpace(network.Ssid))
                    {
                        result.Networks.Add(network);
                    }
                }
            }
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// Scan for Wi-Fi networks and wait for results.
    /// Combines StartWifiScanAsync and GetWifiScanResultsAsync with retry logic.
    /// </summary>
    public async Task<WifiScanResult> ScanWifiNetworksAsync(int maxWaitMs = 5000, int pollIntervalMs = 500)
    {
        // Start the scan
        var started = await StartWifiScanAsync();
        if (!started)
        {
            return new WifiScanResult { ErrorMessage = "Failed to start Wi-Fi scan" };
        }

        // Wait a bit for the scan to complete
        await Task.Delay(1000);

        // Poll for results
        var stopwatch = Stopwatch.StartNew();
        WifiScanResult? result = null;

        while (stopwatch.ElapsedMilliseconds < maxWaitMs)
        {
            result = await GetWifiScanResultsAsync();

            if (result.Success || !result.IsBusy)
            {
                break;
            }

            await Task.Delay(pollIntervalMs);
        }

        return result ?? new WifiScanResult { ErrorMessage = "Scan timed out" };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

public class AtCommandResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonIgnore]
    public bool IsSuccess => Status?.Equals("success", StringComparison.OrdinalIgnoreCase) ?? false;
}

public class WifiScanResult
{
    public bool Success { get; set; }
    public bool IsBusy { get; set; }
    public int TotalCount { get; set; }
    public string? ErrorMessage { get; set; }
    public List<WifiNetwork> Networks { get; set; } = new();
}

public class WifiNetwork
{
    public string Ssid { get; set; } = string.Empty;
    public int Channel { get; set; }
    public int Rssi { get; set; }
    public string AuthMode { get; set; } = string.Empty;

    /// <summary>
    /// Display string for network selection (includes signal strength indicator)
    /// </summary>
    public string DisplayName => $"{Ssid} ({Rssi}dBm)";
}
