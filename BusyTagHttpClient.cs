using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace BusyTag.Lib;

public class BusyTagHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _authToken;

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
