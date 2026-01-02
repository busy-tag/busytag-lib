using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using BusyTag.Lib.Util;
using BusyTag.Lib.Util.DevEventArgs;

namespace BusyTag.Lib;

// All the code in this file is included in all platforms.
public class BusyTagDevice(string? portName)
{
    private const int MaxFilenameLength = 50; // TODO: Need to recheck this
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<bool>? ReceivedDeviceBasicInformation;
    public event EventHandler<LedArgs>? ReceivedSolidColor;
    public event EventHandler<List<PatternLine>>? ReceivedPattern;
    public event EventHandler<WifiConfigArgs>? ReceivedWifiConfig;
    public event EventHandler<bool>? ReceivedUsbMassStorageActive;
    public event EventHandler<int>? ReceivedDisplayBrightness;
    public event EventHandler<List<FileStruct>>? FileListUpdated;
    public event EventHandler<FileUploadFinishedArgs>? FileUploadFinished;
    public event EventHandler<UploadProgressArgs>? FileUploadProgress;
    public event EventHandler<string>? ReceivedShowingPicture;
    public event EventHandler<float>? FirmwareUpdateStatus;
    public event EventHandler<bool>? PlayPatternStatus;
    public event EventHandler<bool>? WritingInStorage;
    private SerialPort? _serialPort;
    private readonly object _lockObject = new object();
    private DeviceConfig _deviceConfig = new();
    public string? PortName { get; } = portName;

    // Centralized data buffer - all serial data flows through sp_DataReceived
    private readonly ConcurrentQueue<byte> _dataBuffer = new();
    private readonly SemaphoreSlim _dataAvailableSemaphore = new(0);

    public bool IsConnected => _serialPort is { IsOpen: true };

    public string DeviceName { get; private set; } = string.Empty;
    public string ManufactureName { get; private set; } = string.Empty;
    public string Id { get; private set; } = string.Empty;
    public string FirmwareVersion { get; private set; } = string.Empty;
    public string HardwareVersion { get; private set; } = string.Empty;
    public float FirmwareVersionFloat { get; private set; }

    public string CurrentImageName { get; private set; } = string.Empty;
    private string CachedFileDirPath { get; set; } = string.Empty;
    public List<FileStruct> FileList { get; set; } = [];
    public string LocalHostAddress { get; private set; } = string.Empty;

    public long FreeStorageSize { get; private set; }
    public long TotalStorageSize { get; private set; }
    private DriveInfo? _busyTagDrive;
    
    private string _currentUploadFileName = string.Empty;

    /// <summary>
    /// Gets the filename currently being uploaded, or empty if no upload in progress.
    /// </summary>
    public string CurrentUploadFileName => _currentUploadFileName;

    private readonly List<PatternLine> _patternList = [];
    private bool _asyncCommandActive;
    private bool _writeRawData;
    private bool _isPlayingPattern;
    private bool _isWritingToStorage;
    private bool _sendingFile;

    /// <summary>
    /// Returns true if the device is currently writing to storage (WIS,1 state)
    /// </summary>
    public bool IsWritingToStorage => _isWritingToStorage;
    private readonly CancellationTokenSource _ctsForConnection = new();
    private CancellationTokenSource _ctsForFileSending = new();

    private void ConnectionTask(int milliseconds, CancellationToken token)
    {
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), token);
                if (!IsConnected)
                {
                    Disconnect();
                }
            }
        }, token);
    }


    public async Task Connect()
    {
        _serialPort = new SerialPort(PortName, 460800, Parity.None, 8, StopBits.One);
        _serialPort.ReadTimeout = 4000;
        _serialPort.WriteTimeout = 4000;
        _serialPort.DtrEnable = false;
        _serialPort.RtsEnable = true;
        _serialPort.Handshake = Handshake.None;
        // _serialPort.ReceivedBytesThreshold = 1;
        _serialPort.WriteBufferSize = 1024 * 1024;
        _serialPort.ReadBufferSize = 1024 * 1024;
        _serialPort.DataReceived += sp_DataReceived;
        _serialPort.ErrorReceived += sp_ErrorReceived;

        try
        {
            _serialPort.Open();
            ConnectionStateChanged?.Invoke(this, IsConnected);
            if (!IsConnected)
            {
                Disconnect();
                return;
            }

            await GetDeviceNameAsync();
            CachedFileDirPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BusyTagImages");
            if (!Directory.Exists(CachedFileDirPath)) Directory.CreateDirectory(CachedFileDirPath);
            
            await GetManufactureNameAsync();
            await GetDeviceIdAsync();
            await GetFirmwareVersionAsync();
            await GetHardwareVersionAsync();
            await GetCurrentImageNameAsync();
            await GetSolidColorAsync();
            await GetDisplayBrightnessAsync();

            if (FirmwareVersionFloat < 2.0)
            {
                await SetUsbMassStorageActiveAsync(true);
                _busyTagDrive = FindBusyTagDrive();
            }
            if (FirmwareVersionFloat > 0.7)
                await SetAllowedAutoStorageScanAsync(false);
            
            await GetTotalStorageSizeAsync();
            await GetFreeStorageSizeAsync();
            await GetFileListAsync();

            // Validate storage integrity shortly after connection (non-blocking)
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // Wait 1 second after connection
                await ValidateStorageIntegrityAsync();
            });

            // Only sync the current image immediately for faster connection
            // if (!string.IsNullOrEmpty(CurrentImageName) && !FileExistsInCache(CurrentImageName))
            // {
            //     await GetFileAsync(CurrentImageName);
            // }

            ReceivedDeviceBasicInformation?.Invoke(this, true);
            ConnectionTask(3000, _ctsForConnection.Token);
        }
        catch (Exception e)
        {
            // ConnectionStateChanged?.Invoke(this, IsConnected);
            Debug.WriteLine($"Error: {e.Message}");
        }
    }

    public void Disconnect()
    {
        try
        {
            _serialPort?.Close();
            _serialPort?.Dispose();
            ClearBuffer(); // Clean up the centralized buffer
        }
        finally
        {
            ConnectionStateChanged?.Invoke(this, false);
            _ctsForConnection.Cancel();
            _serialPort = null;
        }
    }

    public SerialPort? SerialPort()
    {
        return _serialPort;
    }

    private void SendRawData(byte[] data, int offset, int count)
    {
        if (!IsConnected)
        {
            Disconnect();
            throw new InvalidOperationException("Not connected to device");
        }

        lock (_lockObject)
        {
            Debug.WriteLine($"Sending raw data count: {count}");
            _serialPort?.Write(data, offset, count);
        }
    }

    private async Task<string> SendCommandAsync(string command, int timeoutMs = 150, bool waitForFirstResponse = true,
        bool discardInBuffer = true)
    {
        if(_asyncCommandActive || _writeRawData) return string.Empty;
        if (!IsConnected)
        {
            Disconnect();
            return string.Empty;
        }

        try
        {
            string result;
            long timestamp;

            _asyncCommandActive = true;

            lock (_lockObject)
            {
                try
                {
                    // Clear buffer instead of discarding serial port buffer
                    if (discardInBuffer) ClearBuffer();
                    if (!string.IsNullOrEmpty(command)) _serialPort?.WriteLine(command);

                    timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    Debug.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]TX:{command}");
                }
                catch (Exception ex)
                {
                    _asyncCommandActive = false;
                    Debug.WriteLine($"[ERROR] Command '{command}' failed: {ex.Message}");
                    throw new InvalidOperationException($"Command failed: {ex.Message}", ex);
                }
            }

            // Read response from buffer (outside lock to allow sp_DataReceived to run)
            var response = new StringBuilder();
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (GetBufferCount() > 0)
                {
                    Debug.WriteLine($"Buffer has {GetBufferCount()} bytes");
                    var data = await ReadStringFromBufferAsync(50);
                    response.Append(data);

                    var responseStr = response.ToString();
                    if (responseStr.Contains("OK\r\n") || responseStr.Contains("ERROR:") ||
                        responseStr.Contains(">"))
                    {
                        timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        Debug.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{responseStr.Trim()}");
                        _asyncCommandActive = false;
                        return responseStr.Trim();
                    }

                    if (waitForFirstResponse) break;
                }

                await Task.Delay(1);
            }

            result = response.ToString().Trim();
            timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            Debug.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{result}");
            _asyncCommandActive = false;
            return result;
        }
        finally
        {
            _asyncCommandActive = false;
        }
    }

    public async Task<byte[]> SendBytesAsync(byte[]? data, int timeoutMs = 3000, bool discardInBuffer = true)
    {
        if (_asyncCommandActive || _sendingFile || _writeRawData) return [];
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to device");

        try
        {
            _asyncCommandActive = true;

            lock (_lockObject)
            {
                try
                {
                    var isReadOnlyOperation = data == null || data.Length == 0;

                    if (!isReadOnlyOperation)
                    {
                        var preExistingBytes = GetBufferCount();
                        Debug.WriteLine($"preExistingBytes in buffer: {preExistingBytes}");
                        if (preExistingBytes > 0)
                        {
                            if (discardInBuffer) ClearBuffer();
                        }

                        if (data != null) _serialPort?.Write(data, 0, data.Length);
                    }
                }
                catch (Exception ex)
                {
                    _asyncCommandActive = false;
                    Debug.WriteLine($"[SEND BYTES] Exception: {ex.Message}");
                    throw new InvalidOperationException($"Binary operation failed: {ex.Message}", ex);
                }
            }

            // Read response from buffer (outside lock to allow sp_DataReceived to run)
            var responseBuffer = new List<byte>();
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                var currentBytesToRead = GetBufferCount();

                if (currentBytesToRead > 0)
                {
                    Debug.WriteLine($"currentBytesToRead from buffer: {currentBytesToRead}");
                    var buffer = await ReadFromBufferAsync(currentBytesToRead, 100);

                    if (buffer.Length > 0)
                    {
                        responseBuffer.AddRange(buffer);
                    }
                }
                else
                {
                    await Task.Delay(1);
                }
            }

            _asyncCommandActive = false;
            return responseBuffer.ToArray();
        }
        finally
        {
            _asyncCommandActive = false;
        }
    }

    private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null) return;

        const int bufSize = 1024 * 8;
        var buf = new byte[bufSize];
        long timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        try
        {
            // CENTRALIZED READ: This is the ONLY place where we read from the serial port
            var bytesAvailable = _serialPort.BytesToRead;
            if (bytesAvailable <= 0) return;

            Debug.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]sp_DataReceived {bytesAvailable} bytes");

            var len = _serialPort.Read(buf, 0, Math.Min(bufSize, bytesAvailable));

            if (len > 0)
            {
                // Add all received bytes to the centralized buffer
                for (int i = 0; i < len; i++)
                {
                    _dataBuffer.Enqueue(buf[i]);
                }

                // Signal that data is available
                _dataAvailableSemaphore.Release(len);

                var data = Encoding.UTF8.GetString(buf, 0, len);
                // Debug.WriteLine($"[RX {_asyncCommandActive}] {data}");
                // If not in command mode, filter and process async events
                if (!_asyncCommandActive|| data.StartsWith("+evn:"))
                {
                    FilterResponse(data);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[ERROR] sp_DataReceived: {exception.Message}");
        }
    }

    private static void sp_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        Debug.WriteLine(e.ToString());
    }

    /// <summary>
    /// Read data from the centralized buffer (populated by sp_DataReceived).
    /// This is the only proper way to read serial data - never read directly from _serialPort.
    /// </summary>
    private async Task<byte[]> ReadFromBufferAsync(int maxBytes, int timeoutMs)
    {
        var result = new List<byte>();
        var startTime = DateTime.Now;

        while (result.Count < maxBytes && (DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
        {
            // Wait for data to be available (with timeout)
            var remainingTimeout = timeoutMs - (int)(DateTime.Now - startTime).TotalMilliseconds;
            if (remainingTimeout <= 0) break;

            var dataAvailable = await _dataAvailableSemaphore.WaitAsync(Math.Min(remainingTimeout, 10));

            if (dataAvailable && _dataBuffer.TryDequeue(out byte b))
            {
                result.Add(b);
            }
            else if (!dataAvailable)
            {
                // Small delay if no data available yet
                await Task.Delay(1);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Read string data from the centralized buffer.
    /// </summary>
    private async Task<string> ReadStringFromBufferAsync(int timeoutMs)
    {
        var bytes = await ReadFromBufferAsync(1024 * 8, timeoutMs);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Clear the centralized buffer. Use this instead of _serialPort.DiscardInBuffer().
    /// </summary>
    private void ClearBuffer()
    {
        while (_dataBuffer.TryDequeue(out _)) { }

        // Drain the semaphore
        while (_dataAvailableSemaphore.CurrentCount > 0)
        {
            _dataAvailableSemaphore.Wait(0);
        }
    }

    /// <summary>
    /// Get the number of bytes available in the buffer.
    /// </summary>
    private int GetBufferCount()
    {
        return _dataBuffer.Count;
    }

    private void FilterResponse(string data)
    {
        long timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Debug.WriteLine($"FilterResponse [{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{data}");
        var lines = data.Split(['\r', '\n']);
        // Debug.WriteLine($"lines.Length: {lines.Length}");
        foreach (string line in lines)
        {
            // Debug.WriteLine($"line: {line.Trim()}");
            if (line.Length > 1 && line[0] == '+')
            {
                string[] parts = line.Split(':');
                // Debug.WriteLine($"parts[0]: {parts[0]}, parts[1]: {parts[1]}\r\n");
                if (parts.Length > 1)
                {
                    // if (parts[0].Equals("+DN"))
                    // {
                    //     DeviceName = parts[1].Trim();
                    //     LocalHostAddress = $"http://{DeviceName}.local";
                    // }
                    // else if (parts[0].Equals("+MN"))
                    // {
                    //     ManufactureName = parts[1].Trim();
                    // }
                    // else if (parts[0].Equals("+ID"))
                    // {
                    //     Id = parts[1].Trim();
                    // }
                    // else if (parts[0].Equals("+FV"))
                    // {
                    //     FirmwareVersion = parts[1].Trim();
                    //     FirmwareVersionFloat = float.Parse(FirmwareVersion, CultureInfo.InvariantCulture);
                    // }
                    // else if (parts[0].Equals("+PL"))
                    // {
                    // }
                    // else if (parts[0].Equals("+FL"))
                    // {
                    //     _receivingFileList = true;
                    //     var args = parts[1].Split(',');
                    //     if (args.Length >= 3)
                    //     {
                    //         FileList.AddRange(new FileStruct(args[0].Trim(), long.Parse(args[2].Trim())));
                    //     }
                    // }
                    // else if (parts[0].Equals("+GF"))
                    // {
                    //     var args = parts[1].Split(',');
                    //     if (args.Length >= 2)
                    //     {
                    //         _currentlyReceivingFile = new FileStruct(args[0].Trim(), long.Parse(args[1].Trim()));
                    //         _currentlyReceivingFileSize = 0;
                    //         _memoryStream = new MemoryStream();
                    //         _receivingFile = true;
                    //     }
                    // }
                    // else if (parts[0].Equals("+LHA"))
                    // {
                    //     LocalHostAddress = parts[1].Trim();
                    // }
                    // else if (parts[0].Equals("+FSS"))
                    // {
                    //     FreeStorageSize = long.Parse(parts[1]);
                    // }
                    // else if (parts[0].Equals("+TSS"))
                    // {
                    //     TotalStorageSize = long.Parse(parts[1]);
                    // }
                    // else if (parts[0].Equals("+SC"))
                    // {
                    //     var args = parts[1].Split(',');
                    //     if (args.Length >= 2)
                    //     {
                    //         var eventArgs = new LedArgs
                    //         {
                    //             LedBits = int.Parse(args[0].Trim()),
                    //             Color = args[1].Trim()
                    //         };
                    //         ReceivedSolidColor?.Invoke(this, eventArgs);
                    //     }
                    // }
                    // else if (parts[0].Equals("+CP"))
                    // {
                    //     _receivingPattern = true;
                    //     var args = parts[1].Split(',');
                    //     if (args.Length >= 4)
                    //     {
                    //         _patternList.Add(new PatternLine(int.Parse(args[0].Trim()), args[1].Trim(),
                    //             int.Parse(args[2].Trim()), int.Parse(args[3].Trim())));
                    //     }
                    // }
                    // else if (parts[0].Equals("+DB"))
                    // {
                    //     ReceivedDisplayBrightness?.Invoke(this, int.Parse(parts[1].Trim()));
                    // }
                    // else if (parts[0].Equals("+SAD"))
                    // {
                    //     // ReceivedShowAfterDrop?.Invoke(this, int.Parse(parts[1].Trim()) == 1);
                    // }
                    // else if (parts[0].Equals("+AWFS"))
                    // {
                    //     // ReceivedAllowedWebServer?.Invoke(this, int.Parse(parts[1].Trim()) == 1);
                    // }
                    // else if (parts[0].Equals("+WC"))
                    // {
                    //     var args = parts[1].Split(',');
                    //     if (args.Length >= 2)
                    //     {
                    //         var eventArgs = new WifiConfigArgs
                    //         {
                    //             Ssid = args[0].Trim(),
                    //             Password = args[1].Trim()
                    //         };
                    //         ReceivedWifiConfig?.Invoke(this, eventArgs);
                    //     }
                    // }
                    // else if (parts[0].Equals("+UMSA"))
                    // {
                    //     ReceivedUsbMassStorageActive?.Invoke(this, int.Parse(parts[1].Trim()) == 1);
                    // }
                    // else if (parts[0].Equals("+SP"))
                    // {
                    //     CurrentImageName = parts[1].Trim();
                    //     if(!FileExistsInCache(CurrentImageName))
                    //         _ = GetFileAsync(CurrentImageName);
                    //     // GetFile(CurrentImageName);
                    //     // ReceivedShowingPicture?.Invoke(this, CurrentImageName);
                    // }
                    // else 
                    if (parts[0].Equals("+evn"))
                    {
                        var args = parts[1].Split(',');
                        if (args.Length >= 2)
                        {
                            if (args[0].Equals("SP"))
                            {
                                CurrentImageName = args[1].Trim();
                                if (!FileExistsInCache(CurrentImageName))
                                    _ = GetFileAsync(CurrentImageName);
                                ReceivedShowingPicture?.Invoke(this, CurrentImageName);
                            }
                            else if (args[0].Equals("FU"))
                            {
                                var progress = float.Parse(args[1].Remove(args[1].Length - 1));
                                FirmwareUpdateStatus?.Invoke(this, progress);
                            }
                            else if (args[0].Equals("PP"))
                            {
                                _isPlayingPattern = int.Parse(args[1]) != 0;
                                PlayPatternStatus?.Invoke(this, _isPlayingPattern);
                            }
                            else if (args[0].Equals("WIS"))
                            {
                                var isWriting = args[1].Trim() != "0";
                                _isWritingToStorage = isWriting;
                                if (!isWriting) Thread.Sleep(100);
                                WritingInStorage?.Invoke(this, isWriting);
                            }
                        }
                    }
                }
            }
            else
            {
                if (line.Equals("OK"))
                {
                    // Handle OK response
                }
                else if (line.Contains("ERROR"))
                {
                    if (_sendingFile)
                    {
                        var errorMessage = line.Contains(":") ? line.Split(':').Last().Trim() : "Unknown device error";
                        CancelFileUpload(false, UploadErrorType.DeviceError, 
                            $"Device reported error: {errorMessage}");
                    }
                }
            }
        }
    }

    public async Task<string> GetDeviceNameAsync()
    {
        var response = await SendCommandAsync("AT+GDN");
        DeviceName = response.Contains("+DN:busytag-") ? response.Split(':').Last() : "busytag";
        LocalHostAddress = $"http://{DeviceName}.local";
        return DeviceName;
    }

    public async Task<string> GetManufactureNameAsync()
    {
        var response = await SendCommandAsync("AT+GMN");
        ManufactureName = response.Contains("+MN:") ? response.Split(':').Last() : "SIA BUSY TAG";
        return ManufactureName;
    }

    public async Task<string> GetDeviceIdAsync()
    {
        var response = await SendCommandAsync("AT+GID");
        Id = response.Contains("+ID:") ? response.Split(':').Last() : "";
        return Id;
    }

    public async Task<string> GetFirmwareVersionAsync()
    {
        var response = await SendCommandAsync("AT+GFV");
        FirmwareVersion = response.Contains("+FV:") ? response.Split(':').Last() : "";
        FirmwareVersionFloat = float.Parse(FirmwareVersion, CultureInfo.InvariantCulture);
        return FirmwareVersion;
    }

    public async Task<string> GetHardwareVersionAsync()
    {
        var response = await SendCommandAsync("AT+GHV");
        HardwareVersion = response.Contains("+HV:") ? response.Split(':').Last() : "1.0";
        return HardwareVersion;
    }

    public async Task<string> GetCurrentImageNameAsync()
    {
        var response = await SendCommandAsync("AT+SP?");
        CurrentImageName = response.Contains("+SP:") ? response.Split(':').Last() : "";
        if (!string.IsNullOrEmpty(CurrentImageName) && !FileExistsInCache(CurrentImageName))
        {
            await GetFileAsync(CurrentImageName);
        }
        ReceivedShowingPicture?.Invoke(this, CurrentImageName);
        return CurrentImageName;
    }

    public async Task<long> GetFreeStorageSizeAsync()
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            // ReSharper disable once StringLiteralTypo
            var response = await SendCommandAsync("AT+GFSS",200);
            var size = response.Contains("+FSS:") ? response.Split(':').Last() : "0";
            FreeStorageSize = long.Parse(size);
        }
        else
        {
            FreeStorageSize = _busyTagDrive?.TotalFreeSpace ?? 0;
        }

        return FreeStorageSize;
    }

    public async Task<long> GetTotalStorageSizeAsync()
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            // ReSharper disable once StringLiteralTypo
            var response = await SendCommandAsync("AT+GTSS");
            var size = response.Contains("+TSS:") ? response.Split(':').Last() : "0";
            TotalStorageSize = long.Parse(size);
        }
        else
        {
            TotalStorageSize = _busyTagDrive?.TotalSize ?? 0;
        }

        return TotalStorageSize;
    }

    public async Task<List<FileStruct>> GetFileListAsync()
    {
        FileList = new List<FileStruct>();
        if (FirmwareVersionFloat >= 2.0)
        {
            var response = await SendCommandAsync("AT+GFL", 500, false);
            FileList = ParseFileList(response, "+FL:");
        }
        else
        {
            if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
            if (_busyTagDrive != null)
            {
                var di = new DirectoryInfo(_busyTagDrive.Name);
                try
                {
                    var files = di.GetFiles();
                    foreach (var fi in files)
                    {
                        FileList.Add(new FileStruct(fi.Name, fi.Length));
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                }
            }
        }

        FileListUpdated?.Invoke(this, FileList);
        return FileList;
    }

    public async Task<LedArgs> GetSolidColorAsync()
    {
        var response = await SendCommandAsync("AT+SC?");
        var solidColor = ParseSolidColor(response);
        ReceivedSolidColor?.Invoke(this, solidColor);
        return solidColor;
    }

    public async Task<bool> PlayPatternAsync(bool allow, int repeatCount)
    {
        var response = await SendCommandAsync($"AT+PP={(allow ? 1 : 0)},{repeatCount:d}", 60, false);
        if (response.Contains("+PP:"))
        {
            var value = response.Split(':').Last();
            PlayPatternStatus?.Invoke(this, int.Parse(value) != 0);
        }

        return response.Contains("OK");
    }

    public async Task<int> GetDisplayBrightnessAsync()
    {
        var response = await SendCommandAsync("AT+DB?");
        var brightnessString = response.Contains("+DB:") ? response.Split(':').Last() : "";
        var brightness = int.Parse(brightnessString);
        ReceivedDisplayBrightness?.Invoke(this, brightness);
        return brightness;
    }

    public async Task<bool> SetDisplayBrightnessAsync(int brightness)
    {
        if (brightness is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(brightness), "Brightness must be between 0 and 100");

        var response = await SendCommandAsync($"AT+DB={brightness}");
        return response.Contains("OK");
    }

    public async Task<bool> SetUsbMassStorageActiveAsync(bool active)
    {
        // ReSharper disable once StringLiteralTypo
        var response = await SendCommandAsync($"AT+UMSA={(active ? 1 : 0)}");
        return response.Contains("OK");
    }

    public async Task<bool> SetAllowedAutoStorageScanAsync(bool allowed)
    {
        // ReSharper disable once StringLiteralTypo
        var response = await SendCommandAsync($"AT+AASS={(allowed ? 1 : 0)}");
        return response.Contains("OK");
    }

    public async Task<bool> ShowPictureAsync(string fileName)
    {
        var response = await SendCommandAsync($"AT+SP={fileName}", 300);
        if (response.Contains("+evn:SP,"))
        {
            CurrentImageName = fileName;
            if (!string.IsNullOrEmpty(CurrentImageName) && !FileExistsInCache(CurrentImageName))
            {
                await GetFileAsync(CurrentImageName);
            }
            
            ReceivedShowingPicture?.Invoke(this, CurrentImageName);
            return true;
        }

        return response.Contains("OK");
    }

    public async Task<bool> RestartDeviceAsync()
    {
        var response = await SendCommandAsync("AT+RST");
        return response.Contains("OK");
    }

    public async Task<bool> FormatDiskAsync()
    {
        var response = await SendCommandAsync("AT+FD", 2000);
        return response.Contains("OK");
    }

    /// <summary>
    /// Validates storage integrity using multiple checks.
    /// If corruption is detected, formats the disk to fix it.
    /// </summary>
    public async Task ValidateStorageIntegrityAsync()
    {
        try
        {
            var totalFileSizes = FileList.Sum(f => f.Size);
            var usedStorageSize = TotalStorageSize - FreeStorageSize;
            var fileCount = FileList.Count;

            var needsFormat = false;
            var reason = string.Empty;

            // Check 1: Files exist but used space is zero (or vice versa)
            if (fileCount > 0 && usedStorageSize <= 0)
            {
                needsFormat = true;
                reason = $"Files exist ({fileCount}) but used space is zero";
            }
            else if (fileCount == 0 && usedStorageSize > 0)
            {
                // Check 3: No files in list but significant space is used (> 1KB threshold for system overhead)
                const long minThreshold = 1024; // 1KB threshold for system overhead
                if (usedStorageSize > minThreshold)
                {
                    needsFormat = true;
                    reason = $"No files but {usedStorageSize} bytes used (threshold: {minThreshold})";
                }
            }
            else if (fileCount > 0 && totalFileSizes > 0)
            {
                // Check 2: File sizes sum differs significantly from used space
                // Allow 10% tolerance or 64KB (whichever is larger) for filesystem overhead
                var tolerance = Math.Max(usedStorageSize * 0.1, 65536);
                var difference = Math.Abs(totalFileSizes - usedStorageSize);

                if (difference > tolerance)
                {
                    needsFormat = true;
                    reason = $"Size mismatch: files={totalFileSizes}, used={usedStorageSize}, diff={difference}, tolerance={tolerance}";
                }
            }

            if (needsFormat)
            {
                Debug.WriteLine($"Storage integrity issue detected: {reason}. Formatting disk...");
                await FormatDiskAsync();
                // Refresh storage info after format
                await GetTotalStorageSizeAsync();
                await GetFreeStorageSizeAsync();
                await GetFileListAsync();
                Debug.WriteLine("Storage formatted and refreshed successfully.");
            }
            else
            {
                Debug.WriteLine($"Storage integrity OK: {fileCount} files, {totalFileSizes} bytes in files, {usedStorageSize} bytes used");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error validating storage integrity: {ex.Message}");
        }
    }

    public async Task<bool> ActivateFileStorageScanAsync()
    {
        // ReSharper disable once StringLiteralTypo
        var response = await SendCommandAsync("AT+AFSS");
        return response.Contains("OK");
    }

    public async Task<bool> SetWifiStationCredentialsAsync(string ssid, string password)
    {
        var response = await SendCommandAsync($"AT+STA={ssid},{password}", 1000);
        return response.Contains("OK");
    }

    public async Task<bool> SetWifiModeAsync(int mode)
    {
        // Mode: 0=OFF, 1=STA, 2=AP, 3=STA+AP
        if (mode is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(mode), "WiFi mode must be between 0 and 3");

        var response = await SendCommandAsync($"AT+WM={mode}", 2000);
        return response.Contains("OK");
    }

    public async Task<bool> SetSolidColorAsync(string color, int brightness = 100, int ledBits = 127)
    {
        var bright = (int)(brightness * 2.55);
        switch (color)
        {
            case "off":
                return await SendRgbColorAsync(0, 0, 0, ledBits);
            case "red":
                return await SendRgbColorAsync(bright, 0, 0, ledBits);
            case "green":
                return await SendRgbColorAsync(0, bright, 0, ledBits);
            case "blue":
                return await SendRgbColorAsync(0, 0, bright, ledBits);
            case "yellow":
                return await SendRgbColorAsync(bright, bright, 0, ledBits);
            case "cyan":
                return await SendRgbColorAsync(0, bright, bright, ledBits);
            case "magenta":
                return await SendRgbColorAsync(bright, 0, bright, ledBits);
            case "white":
                return await SendRgbColorAsync(bright, bright, bright, ledBits);
            default:
                return await SendRgbColorAsync(0, 0, 0, ledBits);
        }
    }

    public async Task<bool> SendRgbColorAsync(int red = 0, int green = 0, int blue = 0, int ledBits = 127)
    {
        var response = await SendCommandAsync($"AT+SC={ledBits:d},{red:X2}{green:X2}{blue:X2}", waitForFirstResponse: false);
        if (response.Contains("OK"))
        {
            ReceivedSolidColor?.Invoke(this,
                new LedArgs() { LedBits = ledBits, Color = $"{red:X2}{green:X2}{blue:X2}" });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sends a custom AT command to the device and returns the response.
    /// </summary>
    /// <param name="command">The AT command to send (without \r\n)</param>
    /// <param name="timeoutMs">Response timeout in milliseconds (default: 150ms)</param>
    /// <param name="waitForFirstResponse">Whether to wait only for the first response (default: true)</param>
    /// <param name="discardInBuffer">Whether to discard the input buffer before sending (default: true)</param>
    /// <returns>The device response as a string</returns>
    public async Task<string> SendCustomCommandAsync(string command, int timeoutMs = 150,
        bool waitForFirstResponse = true, bool discardInBuffer = true)
    {
        return await SendCommandAsync(command, timeoutMs, waitForFirstResponse, discardInBuffer);
    }

    public async Task<bool> SetNewCustomPattern(List<PatternLine> list, bool playAfterSending, bool playPatternNonStop)
    {
        if (_isPlayingPattern)
        {
            await PlayPatternAsync(false, 3);
            // if (FirmwareVersionFloat < 1.1)
            //     Thread.Sleep(200);
        }

        if (FirmwareVersionFloat > 0.8)
        {
            var response = await SendCommandAsync($"AT+CP={list.Count:d}", 100, false);
            if (response.Contains(">"))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    response = await SendCommandAsync($"+CP:{item.LedBits},{item.Color},{item.Speed},{item.Delay}",
                        (i >= list.Count - 1) ? 200 : 10);
                }

                if (response.Contains("OK"))
                {
                    if (playAfterSending)
                        await PlayPatternAsync(true, (playPatternNonStop ? 255 : 5));
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }
        else
        {
            if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
            if (_busyTagDrive == null) return false;

            GetConfigJsonFile();
            _deviceConfig.ActivatePattern = false;
            _deviceConfig.CustomPatternArr = _patternList;

            var json = JsonSerializer.Serialize(_deviceConfig);
            var fullPath = Path.Combine(_busyTagDrive.Name, "config.json");
            await File.WriteAllTextAsync(fullPath, json);
            _ = await ActivateFileStorageScanAsync();
        }

        return true;
    }

    private static string UnixToDate(long timestamp, string convertFormat)
    {
        var convertedUnixTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
        return convertedUnixTime.ToString(convertFormat);
    }

    private DriveInfo? FindBusyTagDrive()
    {
        var allDrives = DriveInfo.GetDrives();
        foreach (var d in allDrives)
        {
            // Debug.WriteLine($"drive:{d.Name}");
            if (d == null) continue;
            if (!d.IsReady) continue;
            var path = Path.Combine(d.Name, "readme.txt");
            if (!File.Exists(path)) continue;

            // Open the file to read from.
            using var sr = File.OpenText(path);
            while (sr.ReadLine() is { } s)
            {
                if (!s.Contains(LocalHostAddress)) continue;
                return d;
            }
        }

        return null;
    }

    public async Task SendNewFile(string sourcePath)
    {
        // Validate filename length before proceeding
        var fileName = Path.GetFileName(sourcePath);
        _currentUploadFileName = fileName; // Track current upload for FileUploadFinished event
        // Saving in cached dir
        var destFilePath = Path.Combine(CachedFileDirPath, fileName);
        // if (!FileExistsInCache(fileName))
            File.Copy(sourcePath, destFilePath, true);
        _sendingFile = true;
        _currentUploadFileName = fileName;

        var args = new UploadProgressArgs
        {
            FileName = fileName,
            ProgressLevel = 0.0f
        };
        FileUploadProgress?.Invoke(this, args);
        if (fileName.Length > MaxFilenameLength)
        {
            CancelFileUpload(false, UploadErrorType.FilenameToolong, 
                $"Filename '{fileName}' exceeds maximum length of {MaxFilenameLength} characters");
            return;
        }

        _ctsForFileSending = new CancellationTokenSource();
        if (FirmwareVersionFloat >= 2.0)
        {
            await Task.Run(async () =>
            {
                try
                {
                    var success = await SendFileViaSerial(sourcePath);
                    _sendingFile = false;
                    
                    if (success)
                    {
                        FileList.Add(new FileStruct(fileName, new FileInfo(sourcePath).Length));
                        CancelFileUpload(true); // Success
                    }
                    // Error handling is done within SendFileViaSerial
                }
                catch (Exception ex)
                {
                    _sendingFile = false;
                    CancelFileUpload(false, UploadErrorType.Unknown, 
                        $"Unexpected error during file upload: {ex.Message}");
                }
            });
            return;
        }

        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) 
        {
            CancelFileUpload(false, UploadErrorType.DeviceError, 
                "Cannot find BusyTag drive for file transfer");
            return;
        }

        await Task.Run(async () =>
        {
            try
            {
                var destPath = Path.Combine(_busyTagDrive.Name, fileName);
                FileUploadProgress?.Invoke(this, args);

                var isFreeSpaceAvailable = await FreeUpStorage(new FileInfo(sourcePath).Length);
                if (!isFreeSpaceAvailable)
                {
                    CancelFileUpload(false, UploadErrorType.InsufficientStorage, 
                        "Insufficient storage space on device");
                    return;
                }

                await using var fsOut = new FileStream(destPath, FileMode.Create);
                await using var fsIn = new FileStream(sourcePath, FileMode.Open);

#if MACCATALYST
                var buffer = new byte[8192 * 32];
#else
                var buffer = new byte[8192];
#endif
                int readByte;

                while ((readByte = fsIn.Read(buffer, 0, buffer.Length)) > 0 &&
                       !_ctsForFileSending.Token.IsCancellationRequested)
                {
                    try
                    {
                        fsOut.Write(buffer, 0, readByte);
                        args.ProgressLevel = (float)(fsIn.Position * 100.0 / fsIn.Length);
                        FileUploadProgress?.Invoke(this, args);
                    }
                    catch (Exception ex)
                    {
                        _ = await DeleteFile(fileName);
                        CancelFileUpload(false, UploadErrorType.TransferInterrupted, 
                            $"File transfer was interrupted: {ex.Message}");
                        return;
                    }
                }

                if (_ctsForFileSending.Token.IsCancellationRequested)
                {
                    _ = await DeleteFile(fileName);
                    CancelFileUpload(false, UploadErrorType.Cancelled, 
                        "File upload was cancelled by user");
                    return;
                }

                // Success
                _sendingFile = false;
                FileList.Add(new FileStruct(fileName, new FileInfo(sourcePath).Length));
                CancelFileUpload(true); // Success
            }
            catch (Exception ex)
            {
                _sendingFile = false;
                CancelFileUpload(false, UploadErrorType.Unknown, 
                    $"File upload failed: {ex.Message}");
            }
        }, _ctsForFileSending.Token);
    }

    private async Task<bool> SendFileViaSerial(string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var args = new UploadProgressArgs
        {
            FileName = fileName,
            ProgressLevel = 0.0f
        };
        FileUploadProgress?.Invoke(this, args);

       try
        {
            var fileSize = new FileInfo(sourcePath).Length;
            var isFreeSpaceAvailable = await FreeUpStorage(fileSize);
            if (!isFreeSpaceAvailable)
            {
                CancelFileUpload(false, UploadErrorType.InsufficientStorage, 
                    "Insufficient storage space on device");
                return false;
            }

            var data = await File.ReadAllBytesAsync(sourcePath);
            const int chunkSize = 1024 * 8;
            var totalBytesTransferred = 0f;

            var response = await SendCommandAsync($"AT+UF={fileName},{fileSize}", 1000);
            if (!response.Contains(">"))
            {
                CancelFileUpload(false, UploadErrorType.DeviceError, 
                    "Device did not respond properly to upload command");
                return false;
            }

            _writeRawData = true;
            for (var i = 0; i < data.Length; i += chunkSize)
            {
                // Check cancellation token
                if (_ctsForFileSending.Token.IsCancellationRequested)
                {
                    CancelFileUpload(false, UploadErrorType.Cancelled, 
                        "File upload was cancelled by user");
                    return false;
                }

                var remainingBytes = data.Length - i;
                var currentChunkSize = Math.Min(chunkSize, remainingBytes);
                var chunkData = data.Skip(i).Take(currentChunkSize).ToArray();

                // Verify connection before each chunk
                if (!IsConnected)
                {
                    Disconnect();
                    CancelFileUpload(false, UploadErrorType.ConnectionLost, 
                        "Connection to device was lost during upload");
                    return false;
                }

                try
                {
                    SendRawData(chunkData, 0, chunkData.Length);
                    totalBytesTransferred += chunkData.Length;

                    // Calculate progress more precisely
                    var progressLevel = (float)((double)totalBytesTransferred / data.Length * 100.0);
                    args.ProgressLevel = progressLevel;
                    FileUploadProgress?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error sending chunk: {ex.Message}");
                    CancelFileUpload(false, UploadErrorType.TransferInterrupted,
                        $"Data transfer failed: {ex.Message}");
                    return false;
                }
            }
            _writeRawData = false;

            // Final progress update
            args.ProgressLevel = 100.0f;
            FileUploadProgress?.Invoke(this, args);
            
            response = await SendCommandAsync("", 1500, discardInBuffer: false);
            if (response.Contains("OK"))
            {
                return true; // Success - CancelFileUpload will be called with success=true
            }
            else
            {
                CancelFileUpload(false, UploadErrorType.DeviceError, 
                    "Device did not confirm successful upload");
                return false;
            }
        }
        catch (Exception ex)
        {
            CancelFileUpload(false, UploadErrorType.Unknown, 
                $"Upload failed with exception: {ex.Message}");
            return false;
        }
    }

    public void CancelFileUpload(bool successfullyFileUpload = true, UploadErrorType errorType = UploadErrorType.None, 
        string? errorMessage = null)
    {
        _ctsForFileSending.Cancel();
        var args = new FileUploadFinishedArgs
        {
            Success = successfullyFileUpload,
            FileName = _currentUploadFileName,
            ErrorType = errorType,
            ErrorMessage = errorMessage
        };
        _sendingFile =  false;
        FileUploadFinished?.Invoke(this, args);
        
        // Reset upload state
        _currentUploadFileName = string.Empty;
    }

    // Function to delete the oldest image file in the directory
    private static void DeleteOldestFileInDirectory(string directoryPath)
    {
        var directoryInfo = new DirectoryInfo(directoryPath);

        // Get all files in the directory
        var files = directoryInfo.GetFiles()
            .Where(f => f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.CreationTimeUtc) // Sort by the creation time (oldest first)
            .ToList();

        if (!files.Any()) return;
        // Delete the oldest file
        var oldestFile = files.First();
        oldestFile.Delete();
        Debug.WriteLine($"Deleted oldest file: {oldestFile.Name}");
    }

    private async Task<bool> AutoDeleteFile()
    {
        foreach (var item in FileList)
        {
            if (CurrentImageName == item.Name) continue;
            if (item.Name.EndsWith("png") || item.Name.EndsWith("jpg") || item.Name.EndsWith("gif"))
            {
                return await DeleteFile(item.Name);
            }
        }

        return false;
    }

    public async Task<bool> FreeUpStorage(long size)
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            var counter = 0;
            await GetFreeStorageSizeAsync();
            while (FreeStorageSize < size)
            {
                if (await AutoDeleteFile() != true) return false;
                await GetFreeStorageSizeAsync();
                counter++;
                if (counter < 20) continue;
                return false;
            }
        }
        else
        {
            if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
            if (_busyTagDrive == null) return false;
            var counter = 0;
            await GetFreeStorageSizeAsync();
            while (FreeStorageSize < size)
            {
                DeleteOldestFileInDirectory(_busyTagDrive.Name);
                await GetFreeStorageSizeAsync();
                counter++;
                if (counter < 20) continue;
                return false;
            }
        }

        return true;
    }

    public async Task<bool> DeleteFile(string fileName)
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            var response = await SendCommandAsync($"AT+DF={fileName}", 300);
            if (!response.Contains("OK"))
            {
                Debug.WriteLine($"Error deleting file: {fileName}");            
            };
            await GetFreeStorageSizeAsync();
        }
        else
        {
            if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
            if (_busyTagDrive == null) return false;

            var path = Path.Combine(_busyTagDrive.Name, fileName);
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        await GetFileListAsync();
        return true;
    }

    public bool FileExistsInDeviceStorage(string fileName)
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            foreach (var item in FileList)
            {
                if (item.Name == fileName) return true;
            }

            return false;
        }

        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return false;
        var path = Path.Combine(_busyTagDrive.Name, fileName);
        return File.Exists(path);
    }

    public async Task<string> GetFileAsync(string fileName)
    {
        var destFilePath = Path.Combine(CachedFileDirPath, fileName);
        if (FirmwareVersionFloat >= 2.0)
        {
            // Clear any pending data from the buffer
            var pendingBytes = GetBufferCount();
            if (pendingBytes > 0)
            {
                ClearBuffer();
                Debug.WriteLine($"Cleared {pendingBytes} bytes of pending data from buffer");
            }

            // Send download command
            var commandBytes = Encoding.UTF8.GetBytes($"AT+GF={fileName}\r\n");
            var responseBytes = await SendBytesAsync(commandBytes, 1000, false);

            if (responseBytes.Length == 0)
            {
                Debug.WriteLine("No response received for download command");
                return "";
            }

            // Parse response for file size
            var responseText = Encoding.UTF8.GetString(responseBytes);
            var lines = responseText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            var sizeLine = lines.FirstOrDefault(l => l.StartsWith("+GF:"));
            if (sizeLine == null)
            {
                Debug.WriteLine("Invalid download response - no file info found");
                return "";
            }

            var sizeParts = sizeLine.Split(',');
            if (sizeParts.Length < 2 || !long.TryParse(sizeParts[1].Trim(), out var fileSize) || fileSize <= 0)
            {
                Debug.WriteLine($"Invalid file size in response: {sizeLine}");
                return "";
            }

            Debug.WriteLine($"File size: {fileSize:N0} bytes");

            // Find the data start position
            var headerPatterns = new[] { "\r\n\r\n", "\n\n" };
            int dataStart = -1;
            foreach (var pattern in headerPatterns)
            {
                var index = responseText.IndexOf(pattern, StringComparison.Ordinal);
                if (index >= 0)
                {
                    dataStart = index + pattern.Length;
                    break;
                }
            }

            var fileData = new List<byte>();
            long totalBytesReceived = 0;

            // Extract initial data from response
            if (dataStart >= 0 && dataStart < responseBytes.Length)
            {
                var initialDataBytes = responseBytes.Skip(dataStart).ToArray();
                fileData.AddRange(initialDataBytes);
                totalBytesReceived = initialDataBytes.Length;
                Debug.WriteLine($"Progress for {fileName}: {(int)(totalBytesReceived * 100 / fileSize)}%");
                // progress?.Report((int)(totalBytesReceived * 100 / fileSize));
            }

            // Continue reading remaining data
            var downloadTimeout = Math.Max(30, fileSize / 1024 + 15);
            var downloadStartTime = DateTime.Now;
            var zeroSizeResponseCounter = 0;
            const int maxZeroSizeResponses = 10;

            while (totalBytesReceived < fileSize &&
                   (DateTime.Now - downloadStartTime).TotalSeconds < downloadTimeout)
            {
                try
                {
                    var chunk = await SendBytesAsync(null, 100);
                    if (chunk.Length > 0)
                    {
                        fileData.AddRange(chunk);
                        totalBytesReceived += chunk.Length;
                        Debug.WriteLine($"Progress for {fileName}: {(int)(totalBytesReceived * 100 / fileSize)}%");
                        // progress?.Report((int)(totalBytesReceived * 100 / fileSize));

                        // Reset counter on successful read
                        zeroSizeResponseCounter = 0;

                        if (totalBytesReceived >= fileSize)
                            break;
                    }
                    else
                    {
                        zeroSizeResponseCounter++;
                        Debug.WriteLine($"Zero-size response count: {zeroSizeResponseCounter}/{maxZeroSizeResponses}");

                        if (zeroSizeResponseCounter >= maxZeroSizeResponses)
                        {
                            Debug.WriteLine($"Download failed: Received {maxZeroSizeResponses} consecutive zero-size responses");
                            break;
                        }

                        await Task.Delay(1);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Download chunk failed: {ex.Message}");
                    break;
                }
            }

            if (totalBytesReceived >= fileSize)
            {
                Debug.WriteLine($"Serial download completed: {fileName}");
                await File.WriteAllBytesAsync(destFilePath, fileData.Take((int)fileSize).ToArray());
                return destFilePath;
            }

            Debug.WriteLine($"Download incomplete: {totalBytesReceived}/{fileSize} bytes");
            return "";
        }

        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return "";
        var sourceFilePath = Path.Combine(_busyTagDrive.Name, fileName);
        File.Copy(sourceFilePath, destFilePath);
        return destFilePath;
    }

    public bool FileExistsInCache(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var file = Path.Combine(CachedFileDirPath, fileName);
        return File.Exists(file);
    }

    public string GetFilePathFromCache(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        var file = Path.Combine(CachedFileDirPath, fileName);
        if (File.Exists(file))
        {
            return file;
        }

        return "";
    }

    public string GetFullFilePath(string fileName)
    {
        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return string.Empty;
        var path = Path.Combine(_busyTagDrive.Name, fileName);
        return path;
    }

    public FileStruct? GetFileInfo(string fileName)
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            foreach (var item in FileList)
            {
                if (item.Name == fileName) return item;
            }

            return null;
        }

        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return null;
        var path = Path.Combine(_busyTagDrive.Name, fileName);
        var fileInfo = new FileInfo(path);
        var fileStruct = new FileStruct(fileName, fileInfo.Length);
        return fileStruct;
    }

    private void GetConfigJsonFile()
    {
        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return;
        var path = Path.Combine(_busyTagDrive.Name, "config.json");

        if (File.Exists(path))
        {
            // Open the file to read from.
            using var sr = File.OpenText(path);
            var json = sr.ReadToEnd().Trim();

            DeviceConfig? parsedConfig = null;
            try
            {
                parsedConfig = JsonSerializer.Deserialize<DeviceConfig>(json);
            }
            catch
            {
                /* ignored */
            }

            if (parsedConfig != null)
            {
                _deviceConfig = parsedConfig;
                return;
            }
        }

        _deviceConfig = new DeviceConfig
        {
            Version = 3,
            Image = "def.png",
            ShowAfterDrop = false,
            AllowUsbMsc = true,
            AllowFileServer = false,
            DispBrightness = 100,
            solidColor = new DeviceConfig.SolidColor(127, "990000"),
            ActivatePattern = false,
            PatternRepeat = 3
        };
        var lines = new List<PatternLine>
        {
            new(127, "1291AF", 100, 0),
            new(127, "FF0000", 100, 0)
        };
        _deviceConfig.CustomPatternArr = lines;
    }

    private static List<FileStruct> ParseFileList(string response, string prefix)
    {
        var files = new List<FileStruct>();
        var lines = response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Where(l => l.StartsWith(prefix)))
        {
            var data = line.Substring(prefix.Length);
            var parts = data.Split(',');

            if (parts.Length >= 2)
            {
                var file = new FileStruct
                {
                    Name = parts[0],
                    Size = long.TryParse(parts[^1], out var size) ? size : 0,
                };

                files.Add(file);
            }
        }

        return files;
    }

    private static LedArgs ParseSolidColor(string response)
    {
        var value = response.Contains("+SC:") ? response.Split(':').Last() : "";
        var parts = value.Split(',');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var ledBits))
        {
            var colorHex = parts[1];
            return new LedArgs()
            {
                LedBits = ledBits,
                Color = colorHex
            };
        }

        return new LedArgs() { LedBits = 127, Color = "990000" };
    }

    private static WiFiConfig ParseWiFiConfig(string response)
    {
        var value = response.Contains("+WC:") ? response.Split(':').Last() : "";
        var parts = value.Split(',');

        return new WiFiConfig
        {
            Ssid = parts.Length > 0 ? parts[0] : "",
            Password = parts.Length > 1 ? parts[1] : ""
        };
    }

    private static List<PatternLine> ParseCustomPattern(string response)
    {
        var patterns = new List<PatternLine>();
        var lines = response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Where(l => l.StartsWith("+CP:")))
        {
            var data = line.Substring(4);
            var parts = data.Split(',');

            if (parts.Length >= 4)
            {
                patterns.Add(new PatternLine(int.Parse(parts[0].Trim()), parts[1].Trim(),
                    int.Parse(parts[2].Trim()), int.Parse(parts[3].Trim())));
            }
        }

        return patterns;
    }
}