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
    private const int MaxFilenameLength = 40; // TODO: Need to recheck this
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<bool>? ReceivedDeviceBasicInformation;
    public event EventHandler<LedArgs>? ReceivedSolidColor;
    public event EventHandler<List<PatternLine>>? ReceivedPattern;
    public event EventHandler<bool>? ReceivedShowAfterDrop;
    public event EventHandler<bool>? ReceivedAllowedWebServer;
    public event EventHandler<WifiConfigArgs>? ReceivedWifiConfig;
    public event EventHandler<bool>? ReceivedUsbMassStorageActive;
    public event EventHandler<int>? ReceivedDisplayBrightness;
    public event EventHandler<List<FileStruct>>? FileListUpdated;
    public event EventHandler<bool>? FileUploadFinished;
    public event EventHandler<UploadProgressArgs>? FileUploadProgress;
    public event EventHandler<string>? ReceivedShowingPicture;
    public event EventHandler<float>? FirmwareUpdateStatus;
    public event EventHandler<bool>? PlayPatternStatus;
    public event EventHandler<bool>? WritingInStorage;
    private SerialPort? _serialPort;
    private readonly object _lockObject = new object();
    private DeviceConfig _deviceConfig = new();
    public string? PortName { get; } = portName;

    public bool IsConnected => _serialPort is { IsOpen: true };

    public string DeviceName { get; private set; } = string.Empty;
    public string ManufactureName { get; private set; } = string.Empty;
    public string Id { get; private set; } = string.Empty;

    public string FirmwareVersion { get; private set; } = string.Empty;
    public float FirmwareVersionFloat { get; private set; }
    public string CurrentImageName { get; private set; } = string.Empty;
    public string CurrentImagePath { get; private set; } = string.Empty;
    private string CachedImageDirPath { get; set; } = string.Empty;

    // public FileStruct[] PictureList { get; private set; }
    private List<FileStruct> FileList { get; set; } = [];
    public string LocalHostAddress { get; private set; } = string.Empty;

    public long FreeStorageSize { get; private set; }
    public long TotalStorageSize { get; private set; }
    private static string _currentlySendingFile = string.Empty;
    private FileStruct? _currentlyReceivingFile;
    private long _currentlyReceivingFileSize;
    private MemoryStream _memoryStream = new MemoryStream();
    private DriveInfo? _busyTagDrive;
    private readonly List<PatternLine> _patternList = [];
    
    private bool _gotAllBasicInfo;
    private bool _receivingFileList;
    private bool _receivingFile;
    private bool _sendingFile;
    private bool _needToWaitOk;
    private bool _receivingPattern;
    private bool _sendingNewPattern;
    private bool _isPlayingPattern = false;
    private bool _playPatternAfterSending = false;
    private bool _playPatternNonStop = false;
    private bool _skipChecking = false;
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
#if MACCATALYST
                if (_gotAllBasicInfo && !_skipChecking)
                {
                    try
                    {
                        // Send a simple command to check if the device is responsive
                        _serialPort?.WriteLine("AT\r\n");
                        // SendCommand("AT\r\n");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Exception in connection check: {ex.Message}");
                        Disconnect();
                    }
                }

                _skipChecking = false;
#endif
            }
        }, token);
    }


    public async Task Connect()
    {
        _serialPort = new SerialPort(PortName, 460800, Parity.None, 8, StopBits.One);
        _serialPort.ReadTimeout = 2000;
        _serialPort.WriteTimeout = 2000;
        _serialPort.DtrEnable = true;
        _serialPort.RtsEnable = true;
        _serialPort.WriteBufferSize = 1024 * 1024;
        _serialPort.ReadBufferSize = 1024 * 1024;
        // _serialPort.DataReceived += sp_DataReceived;
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

            _gotAllBasicInfo = false;

            DeviceName = await GetDeviceNameAsync();
            LocalHostAddress = $"http://{DeviceName}.local";
            CachedImageDirPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusyTagImages");
            if (!Directory.Exists(CachedImageDirPath)) Directory.CreateDirectory(CachedImageDirPath);
            CachedImageDirPath = Path.Combine(CachedImageDirPath, DeviceName);
            if (!Directory.Exists(CachedImageDirPath)) Directory.CreateDirectory(CachedImageDirPath);
            ManufactureName = await GetManufactureNameAsync();
            Id = await GetDeviceIdAsync();
            FirmwareVersion = await GetFirmwareVersionAsync();
            FirmwareVersionFloat = float.Parse(FirmwareVersion, CultureInfo.InvariantCulture);
            CurrentImageName = await GetCurrentImageNameAsync();
            SetUsbMassStorageActive(true);
            
            if (FirmwareVersionFloat > 0.7)
            {
                SetAllowedAutoStorageScan(false);
            }

            if (FirmwareVersionFloat < 2.0)
                _busyTagDrive = FindBusyTagDrive();
            
            _gotAllBasicInfo = true;
            
            _serialPort.DataReceived += sp_DataReceived;
            
            GetFreeStorageSize();
            Task.Delay(20).Wait();
            GetTotalStorageSize();
            Task.Delay(20).Wait();
            // GetFileList();
            TryToGetFileList();
            GetFile(CurrentImageName);
            
            ReceivedDeviceBasicInformation?.Invoke(this, _gotAllBasicInfo);
            // _currentCommand = SerialPortCommands.Commands.GetDeviceName;
            // SendCommand(Commands.GetCommand(_currentCommand));
            // ConnectionTask(3000, _ctsForConnection.Token);
        }
        catch (Exception e)
        {
            // ConnectionStateChanged?.Invoke(this, IsConnected);
            Trace.WriteLine($"Error: {e.Message}");
        }
    }

    public void Disconnect()
    {
        try
        {
            _serialPort?.Close();
            _serialPort?.Dispose(); // this is still necessary?
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
            _serialPort?.Write(data, offset, count);
        }
    }
    
    private bool SendCommand(string data)
    {
        if (!IsConnected)
        {
            Disconnect();
            return false;
            // throw new InvalidOperationException("Not connected to device");
        }
        
        if (_sendingFile || _sendingNewPattern || _receivingFile) return false;
        
        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]TX:{data}");
        try
        {
            _serialPort?.WriteLine(data);
            _skipChecking = true;
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
        }

        return true;
    }

    private Task<string> SendCommandAsync(string command, int timeoutMs = 40)
    {
        if (!IsConnected)
        {
            Disconnect();
            return Task.FromResult<string>(null);
            // throw new InvalidOperationException("Not connected to device");
        }
        
        try
        {
            string result;
            long timestamp;
            lock (_lockObject)
            {
                try
                {
                    _serialPort?.DiscardInBuffer();
                    
                    if (!string.IsNullOrEmpty(command))
                    {
                        _serialPort?.WriteLine(command);
                    }
                    
                    timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]TX:{command}");

                    var response = new StringBuilder();
                    var startTime = DateTime.Now;

                    while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
                    {
                        if (_serialPort?.BytesToRead > 0)
                        {
                            var data = _serialPort.ReadExisting();
                            response.Append(data);

                            var responseStr = response.ToString();
                            if (responseStr.Contains("OK\r\n") || responseStr.Contains("ERROR:") ||
                                responseStr.Contains(">"))
                            {
                                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                                Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{responseStr.Trim()}");
                                return Task.FromResult(responseStr.Trim());
                            }
                        }

                        Thread.Sleep(10);
                    }

                    result = response.ToString().Trim();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Command '{command}' failed: {ex.Message}");
                    throw new InvalidOperationException($"Command failed: {ex.Message}", ex);
                }
            }
            timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{result.Trim()}");
            return Task.FromResult(result);
        }
        finally
        {
        }
    }

    private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null) return; // TODO Possibly change to exception

        // string data = _serialPort.ReadLine();
        const int bufSize = 1024 * 8;
        var buf = new byte[bufSize];
        // string data = _serialPort.ReadLine();
        // ReSharper disable once UnusedVariable
        try
        {
            var len = _serialPort?.Read(buf, 0, bufSize);
            // long timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            // Trace.WriteLine($"timestamp: {timestamp}, len:{len}");
            var data = System.Text.Encoding.UTF8.GetString(buf, 0, buf.Length);

            if (_receivingFile)
            {
                if (len != null)
                {
                    if (data.Contains("ERROR"))
                    {
                        _memoryStream.Dispose();
                        _receivingFile = false;
                        return;
                    }
                    _currentlyReceivingFileSize += (int)len;
                    _memoryStream.Write(buf, 0, (int)len);
                }
                if (_currentlyReceivingFile != null && _currentlyReceivingFileSize >= _currentlyReceivingFile.Value.Size)
                {
                    string outputFilePath = Path.Combine(CachedImageDirPath, _currentlyReceivingFile.Value.Name);
                    CurrentImagePath =  outputFilePath;
                    Trace.WriteLine($"Saving {_currentlyReceivingFile.Value.Name} to {CurrentImagePath}");
                    File.WriteAllBytes(outputFilePath, _memoryStream.ToArray());
                    ReceivedShowingPicture?.Invoke(this, CurrentImageName);
                    _receivingFile = false;
                }
                return;
            } 
            FilterResponse(data);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
        }
    }

    private static void sp_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        Trace.WriteLine(e.ToString());
    }

    private void FilterResponse(string data)
    {
        long timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{data}");
        var lines = data.Split(['\r', '\n']);
        // Trace.WriteLine($"lines.Length: {lines.Length}");
        foreach (string line in lines)
        {
            // Trace.WriteLine($"line: {line.Trim()}");
            if (line.Length > 1 && line[0] == '+')
            {
                string[] parts = line.Split(':');
                // Trace.WriteLine($"parts[0]: {parts[0]}, parts[1]: {parts[1]}\r\n");
                if (parts.Length > 1)
                {
                    if (parts[0].Equals("+DN"))
                    {
                        DeviceName = parts[1].Trim();
                        LocalHostAddress = $"http://{DeviceName}.local";
                    }
                    else if (parts[0].Equals("+MN"))
                    {
                        ManufactureName = parts[1].Trim();
                    }
                    else if (parts[0].Equals("+ID"))
                    {
                        Id = parts[1].Trim();
                    }
                    else if (parts[0].Equals("+FV"))
                    {
                        FirmwareVersion = parts[1].Trim();
                        FirmwareVersionFloat = float.Parse(FirmwareVersion, CultureInfo.InvariantCulture);
                    }
                    else if (parts[0].Equals("+PL"))
                    {
                    }
                    else if (parts[0].Equals("+FL"))
                    {
                        _receivingFileList = true;
                        var args = parts[1].Split(',');
                        if (args.Length >= 3)
                        {
                            FileList.AddRange(new FileStruct(args[0].Trim(), long.Parse(args[2].Trim())));
                        }
                    }
                    else if (parts[0].Equals("+GF"))
                    {
                        var args = parts[1].Split(',');
                        if (args.Length >= 2)
                        {
                            _currentlyReceivingFile = new FileStruct(args[0].Trim(), long.Parse(args[1].Trim()));
                            _currentlyReceivingFileSize = 0;
                            _memoryStream = new MemoryStream();
                            _receivingFile = true;
                        }
                    }
                    // else if (parts[0].Equals("+LHA"))
                    // {
                    //     LocalHostAddress = parts[1].Trim();
                    // }
                    else if (parts[0].Equals("+FSS"))
                    {
                        FreeStorageSize = long.Parse(parts[1]);
                    }
                    else if (parts[0].Equals("+TSS"))
                    {
                        TotalStorageSize = long.Parse(parts[1]);
                    }
                    else if (parts[0].Equals("+SC"))
                    {
                        var args = parts[1].Split(',');
                        if (args.Length >= 2)
                        {
                            var eventArgs = new LedArgs
                            {
                                LedBits = int.Parse(args[0].Trim()),
                                Color = args[1].Trim()
                            };
                            ReceivedSolidColor?.Invoke(this, eventArgs);
                        }
                    }
                    else if (parts[0].Equals("+CP"))
                    {
                        _receivingPattern = true;
                        var args = parts[1].Split(',');
                        if (args.Length >= 4)
                        {
                            _patternList.Add(new PatternLine(int.Parse(args[0].Trim()), args[1].Trim(),
                                int.Parse(args[2].Trim()), int.Parse(args[3].Trim())));
                        }
                    }
                    else if (parts[0].Equals("+DB"))
                    {
                        ReceivedDisplayBrightness?.Invoke(this, int.Parse(parts[1].Trim()));
                    }
                    else if (parts[0].Equals("+SAD"))
                    {
                        ReceivedShowAfterDrop?.Invoke(this, int.Parse(parts[1].Trim()) == 1);
                    }
                    else if (parts[0].Equals("+AWFS"))
                    {
                        ReceivedAllowedWebServer?.Invoke(this, int.Parse(parts[1].Trim()) == 1);
                    }
                    else if (parts[0].Equals("+WC"))
                    {
                        var args = parts[1].Split(',');
                        if (args.Length >= 2)
                        {
                            var eventArgs = new WifiConfigArgs
                            {
                                Ssid = args[0].Trim(),
                                Password = args[1].Trim()
                            };
                            ReceivedWifiConfig?.Invoke(this, eventArgs);
                        }
                    }
                    else if (parts[0].Equals("+UMSA"))
                    {
                        ReceivedUsbMassStorageActive?.Invoke(this, int.Parse(parts[1].Trim()) == 1);
                    }
                    else if (parts[0].Equals("+SP"))
                    {
                        CurrentImageName = parts[1].Trim();
                        GetFile(CurrentImageName);
                        // ReceivedShowingPicture?.Invoke(this, CurrentImageName);
                    }
                    else if (parts[0].Equals("+evn"))
                    {
                        _skipChecking = true;
                        var args = parts[1].Split(',');
                        if (args.Length >= 2)
                        {
                            if (args[0].Equals("SP"))
                            {
                                CurrentImageName = args[1].Trim();
                                GetFile(CurrentImageName);
                                // ReceivedShowingPicture?.Invoke(this, CurrentImageName);
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
                    // Trace.WriteLine("Received: OK");
                    if (_receivingPattern)
                    {
                        // Trace.WriteLine($"_receivingPattern: {_receivingPattern}");
                        ReceivedPattern?.Invoke(this, _patternList);
                        _receivingPattern = false;
                        if (_playPatternAfterSending)
                        {
                            PlayPattern(true, (_playPatternNonStop ? 255 : 5));
                        }
                    } else if (_receivingFileList)
                    {
                        FileListUpdated?.Invoke(this, FileList);
                        _receivingFileList = false;
                    }else if (_needToWaitOk)
                    {
                        _needToWaitOk =  false;
                        _sendingFile = false;
                        FileUploadFinished?.Invoke(this, true);
                        GetFileList();
                    }
                }
                else if (line.Contains("ERROR"))
                {
                    if (_sendingFile)
                    {
                        _sendingFile = false;
                        FileUploadFinished?.Invoke(this, false);
                        GetFileList();
                    }
                }
                else if (line.Equals(">") && _sendingNewPattern)
                {
                    var patternListCopy = new List<PatternLine>(_patternList);
                    foreach (var item in patternListCopy)
                    {
                        SendCommand($"+CP:{item.LedBits},{item.Color},{item.Speed},{item.Delay}\r\n");
                    }

                    _sendingNewPattern = false;
                    if (_playPatternAfterSending)
                    {
                        PlayPattern(true, (_playPatternNonStop ? 255 : 5));
                    }
                }else if (line.Equals(">") && _sendingFile)
                {
                    _ = ProceedingSendingFile(_currentlySendingFile);
                }
            }
        }

        // if (!_gotAllBasicInfo)
        // {
        //     _currentCommand++;
        //     if (_currentCommand > SerialPortCommands.Commands.GetShowingPicture)
        //     {
        //         if (!_gotDriveInfo)
        //         {
        //             if (FirmwareVersionFloat > 0.7)
        //             {
        //                 SetAllowedAutoStorageScan(false);
        //             }
        //
        //             _gotDriveInfo = true;
        //             _busyTagDrive = FindBusyTagDrive();
        //             TryToGetFileList();
        //         }
        //
        //         _gotAllBasicInfo = true;
        //         ReceivedDeviceBasicInformation?.Invoke(this, _gotAllBasicInfo);
        //     }
        //     else
        //     {
        //         SendCommand(Commands.GetCommand(_currentCommand));
        //     }
        // }
    }

    public async Task<string> GetDeviceNameAsync()
    {
        var response = await SendCommandAsync("AT+GDN\r\n", 30);
        return response.Contains("+DN:busytag-") ? response.Split(':').Last() : "";
    }

    public async Task<string> GetManufactureNameAsync()
    {
        var response = await SendCommandAsync("AT+GMN\r\n", 30);
        return response.Contains("+MN:") ? response.Split(':').Last() : "";
    }

    public async Task<string> GetDeviceIdAsync()
    {
        var response = await SendCommandAsync("AT+GID\r\n", 30);
        return response.Contains("+ID:") ? response.Split(':').Last() : "";
    }

    public async Task<string> GetFirmwareVersionAsync()
    {
        var response = await SendCommandAsync("AT+GFV\r\n", 30);
        return response.Contains("+FV:") ? response.Split(':').Last() : "";
    }

    public async Task<string> GetCurrentImageNameAsync()
    {
        var response = await SendCommandAsync("AT+SP?\r\n", 30);
        return response.Contains("+SP:") ? response.Split(':').Last() : "";
    }

    // public void GetPictureList()
    // {
    //     SendCommand("AT+GPL\r\n");
    // }
    
    private void GetFileList()
    {
        FileList = new List<FileStruct>();
        SendCommand("AT+GFL\r\n");
    }

    public void GetFreeStorageSize()
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand("AT+GFSS\r\n");
    }
    
    public void GetTotalStorageSize()
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand("AT+GTSS\r\n");
    }
    // public long FreeStorageSize()
    // {
    //     return _busyTagDrive?.TotalFreeSpace ?? 0;
    // }
    //
    // public long TotalStorageSize()
    // {
    //     return _busyTagDrive?.TotalSize ?? 0;
    // }

    public void GetSolidColor()
    {
        SendCommand("AT+SC?\r\n");
    }

    public void GetCustomPattern()
    {
        SendCommand("AT+CP?\r\n" );
        _patternList.Clear();
    }

    public void GetDisplayBrightness()
    {
        SendCommand("AT+DB?\r\n" );
    }

    public void GetShowingPicture()
    {
        SendCommand("AT+SP?\r\n");
    }

    public void GetShowAfterDrop()
    {
        SendCommand("AT+SAD?\r\n");
    }

    public void GetAllowedWebServer()
    {
        SendCommand("AT+AWFS?\r\n");
    }

    public void GetWifiConfig()
    {
        SendCommand("AT+WC?\r\n");
    }

    public void GetUsbMassStorageActive()
    {
        SendCommand("AT+UMSA?\r\n");
    }

    public void SetSolidColor(string color, int brightness = 100, int ledBits = 127)
    {
        var bright = (int)(brightness * 2.55);
        switch (color)
        {
            case "off":
                SendRgbColor(0, 0, 0, ledBits);
                break;
            case "red":
                SendRgbColor(bright, 0, 0, ledBits);
                break;
            case "green":
                SendRgbColor(0, bright, 0, ledBits);
                break;
            case "blue":
                SendRgbColor(0, 0, bright, ledBits);
                break;
            case "yellow":
                SendRgbColor(bright, bright, 0, ledBits);
                break;
            case "cyan":
                SendRgbColor(0, bright, bright, ledBits);
                break;
            case "magenta":
                SendRgbColor(bright, 0, bright, ledBits);
                break;
            case "white":
                SendRgbColor(bright, bright, bright, ledBits);
                break;
            default:
                SendRgbColor(0, 0, 0, ledBits);
                break;
        }
    }

    public void SendRgbColor(int red = 0, int green = 0, int blue = 0, int ledBits = 127)
    {
        SendCommand($"AT+SC={ledBits:d},{red:X2}{green:X2}{blue:X2}\r\n");
        var eventArgs = new LedArgs
        {
            LedBits = ledBits,
            Color = $"{red:X2}{green:X2}{blue:X2}"
        };
        ReceivedSolidColor?.Invoke(this, eventArgs);
    }

    public void SetNewCustomPattern(List<PatternLine> list, bool playAfterSending, bool playPatternNonStop)
    {
        if (_isPlayingPattern)
        {
            PlayPattern(false, 3);
            if (FirmwareVersionFloat < 1.1)
                Thread.Sleep(200);
        }

        _playPatternAfterSending = playAfterSending;
        _playPatternNonStop = playPatternNonStop;
        _patternList.Clear();
        foreach (var item in list)
        {
            _patternList.Add(item);
        }

        if (FirmwareVersionFloat > 0.8)
        {
            SendCommand($"AT+CP={list.Count:d}\r\n");
            _sendingNewPattern = true;
        }
        else
        {
            if (_busyTagDrive == null) return; // TODO Possibly change to exception

            GetConfigJsonFile();
            _deviceConfig.ActivatePattern = false;
            _deviceConfig.CustomPatternArr = _patternList;

            var json = JsonSerializer.Serialize(_deviceConfig);
            var fullPath = Path.Combine(_busyTagDrive.Name, "config.json");
            File.WriteAllText(fullPath, json);
            // PlayPattern(false, _deviceConfig.patternRepeat);
            ActivateFileStorageScan();
        }
    }

    public void PlayPattern(bool allow, int repeatCount)
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand($"AT+PP={(allow ? 1 : 0)},{repeatCount:d}\r\n");
    }

    public void SetDisplayBrightness(int brightness)
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand($"AT+DB={brightness}\r\n");
    }

    public void SetAllowedAutoStorageScan(bool enabled)
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand($"AT+AASS={(enabled ? 1 : 0)}\r\n");
    }

    private static string UnixToDate(long timestamp, string convertFormat)
    {
        var convertedUnixTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
        return convertedUnixTime.ToString(convertFormat);
    }

    private DriveInfo? FindBusyTagDrive()
    {
        if (LocalHostAddress == null) return null;
        var allDrives = DriveInfo.GetDrives();
        foreach (var d in allDrives)
        {
            // Trace.WriteLine($"drive:{d.Name}");
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

    public void SendNewFile(string sourcePath)
    {
        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return;

        _ctsForFileSending = new CancellationTokenSource();
        Task.Run(async () =>
        {
            //TODO: need to recheck if sourcePath and destPath is not the same
            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(_busyTagDrive.Name, fileName);
            var isFreeSpaceAvailable = await FreeUpStorage(new FileInfo(sourcePath).Length);
            if (!isFreeSpaceAvailable)
            {
                CancelFileUpload();
            }

            File.Copy(sourcePath, destPath, true);
            FileUploadFinished?.Invoke(this, true);

            CancelFileUpload();
            return Task.CompletedTask;
        }, _ctsForFileSending.Token);
    }

    public async Task SendNewFileWithProgressEvents(string sourcePath)
    {
        // Validate filename length before proceeding
        var fileName = Path.GetFileName(sourcePath);
        var args = new UploadProgressArgs
        {
            FileName = fileName,
            ProgressLevel = 0.0f
        };
        FileUploadProgress?.Invoke(this, args);
        if (fileName.Length > MaxFilenameLength)
        {
            FileUploadFinished?.Invoke(this, false);
            return;
        }

        _ctsForFileSending = new CancellationTokenSource();
        if (FirmwareVersionFloat >= 2.0)
        {
            await SendFileViaSerial(sourcePath);
            return;
        }
        
        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return;

        await Task.Run(async () =>
        {
            var destPath = Path.Combine(_busyTagDrive.Name, fileName);
            args = new UploadProgressArgs
            {
                FileName = fileName,
                ProgressLevel = 0.0f
            };
            FileUploadProgress?.Invoke(this, args);

            var isFreeSpaceAvailable = await FreeUpStorage(new FileInfo(sourcePath).Length);
            if (!isFreeSpaceAvailable)
            {
                CancelFileUpload();
                return;
            }

            await using var fsOut = new FileStream(destPath, FileMode.Create);
            await using var fsIn = new FileStream(sourcePath, FileMode.Open);

            if (fsOut == null || fsIn == null)
            {
                CancelFileUpload();
                return;
            }

#if MACCATALYST
            var buffer = new byte[8192 * 32];
#else
            var buffer = new byte[8192];
#endif
            int readByte;

            while ((readByte = fsIn.Read(buffer, 0, buffer.Length)) > 0 &&
                   _ctsForFileSending.Token.IsCancellationRequested == false)
            {
                try
                {
                    fsOut.Write(buffer, 0, readByte);
                    args.ProgressLevel = (float)(fsIn.Position * 100.0 / fsIn.Length);
                    FileUploadProgress?.Invoke(this, args);
                }
                catch (Exception)
                {
                    DeleteFile(fileName);
                    FileUploadFinished?.Invoke(this, false);
                    CancelFileUpload();
                }
            }

            FileUploadFinished?.Invoke(this, true);

            CancelFileUpload();
        }, _ctsForFileSending.Token);
    }

    private async Task SendFileViaSerial(string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var args = new UploadProgressArgs
        {
            FileName = fileName,
            ProgressLevel = 0.0f
        };
        FileUploadProgress?.Invoke(this, args);
        
        var fileSize = new FileInfo(sourcePath).Length;
        var isFreeSpaceAvailable = await FreeUpStorage(fileSize);
        if (!isFreeSpaceAvailable)
        {
            CancelFileUpload();
            return;
        }

        // await using var fsIn = new FileStream(sourcePath, FileMode.Open);

        // if (fsIn == null)
        // {
        //     _ctsForFileSending.Cancel();
        //     return;
        // }
        
        SendCommand($"AT+UF={fileName},{fileSize}\r\n");
        _currentlySendingFile = sourcePath;
        _sendingFile = true;
    }

    private async Task ProceedingSendingFile(string sourcePath)
    {
        var data = await File.ReadAllBytesAsync(sourcePath);
        const int chunkSize = 1024 * 8;
        var totalBytesTransferred = 0f;
        var fileName = Path.GetFileName(sourcePath);
        var args = new UploadProgressArgs
        {
            FileName = fileName,
            ProgressLevel = 0.0f
        };
        FileUploadProgress?.Invoke(this, args);
        for (var i = 0; i < data.Length && _sendingFile; i += chunkSize)
        {
            var chunkData = data.Skip(i).Take(Math.Min(chunkSize, data.Length - i)).ToArray();
            // Verify connection before each chunk
            if (!IsConnected)
            {
                _sendingFile = false;
                FileUploadFinished?.Invoke(this, false);
                Disconnect();
                CancelFileUpload();
            }
            SendRawData(chunkData, 0, chunkData.Length);
            totalBytesTransferred += chunkData.Length;
            args.ProgressLevel = (float)(totalBytesTransferred / data.Length)*100f;
            FileUploadProgress?.Invoke(this, args);
        }

        _needToWaitOk = true;
        CancelFileUpload();
    }

    public void CancelFileUpload()
    {
        _ctsForFileSending.Cancel();
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
        Trace.WriteLine($"Deleted oldest file: {oldestFile.Name}");
    }

    private void AutoDeleteFile()
    {
        foreach (var item in FileList)
        {
            if(CurrentImageName == item.Name) continue;
            if (item.Name.EndsWith("png") || item.Name.EndsWith("jpg") || item.Name.EndsWith("gif"))
            {
                DeleteFile(item.Name);
            }
        }
    }

    public async Task<bool> FreeUpStorage(long size)
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            var counter = 0;
            GetFreeStorageSize();
            Task.Delay(20).Wait();
            while (FreeStorageSize < size)
            {
                AutoDeleteFile();
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
            GetFreeStorageSize();
            Task.Delay(20).Wait();
            while (FreeStorageSize < size)
            {
                DeleteOldestFileInDirectory(_busyTagDrive.Name);
                counter++;
                if (counter < 20) continue;
                return false;
            }
        }
        
        return true;
    }

    public void TryToGetFileList()
    {
        FileList = new List<FileStruct>();
        if (FirmwareVersionFloat >= 2.0)
        {
            GetFileList();
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
                    FileListUpdated?.Invoke(this, FileList);
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.Message);
                }
            }
        }
    }

    public void DeleteFile(string fileName)
    {
        if (FirmwareVersionFloat >= 2.0)
        {
            SendCommand($"AT+DF={fileName}\r\n");
            // GetFileList();
            GetFreeStorageSize();
        }
        else
        {
            if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
            if (_busyTagDrive == null) return; // TODO Possibly change to exception

            var path = Path.Combine(_busyTagDrive.Name, fileName);
            try
            {
                // Trace.WriteLine($"Deleting file: {path}");       
                File.Delete(path);
            }
            catch (Exception)
            {
                // ignored
            }
        }
        
        TryToGetFileList();
    }

    public MemoryStream GetImage(string fileName)
    {
        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
        if (_busyTagDrive == null) return new MemoryStream(); // TODO Possibly change to exception

        var path = Path.Combine(_busyTagDrive.Name, fileName);
        var imageStream = new MemoryStream();
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.CopyTo(imageStream);
            imageStream.Seek(0, SeekOrigin.Begin);
        }
        catch
        {
            Trace.WriteLine("Had an error while trying to read image");
        }

        return imageStream;
    }

    public bool FileExists(string fileName)
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

    public string GetFile(string fileName)
    {
        // if(string.IsNullOrEmpty(fileName)) return null;
        var file = Path.Combine(CachedImageDirPath, fileName);
        if (File.Exists(file))
        {
            foreach (var item in FileList)
            {
                if (item.Name == fileName)
                {
                    if (item.Size != new FileInfo(file).Length)
                    {
                        if (FirmwareVersionFloat >= 2.0)
                        {
                            Trace.WriteLine("Trying to restore in cache from busy tag serial");
                            SendCommand($"AT+GF={fileName}\r\n");
                            return "";
                        }

                        Trace.WriteLine("Trying to restore in cache from busy tag drive");
                        if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
                        if (_busyTagDrive == null) return "";
                        var path = Path.Combine(_busyTagDrive.Name, fileName);
                        File.Copy(path, file);
                        CurrentImagePath = file;
                        CurrentImageName = fileName;
                        ReceivedShowingPicture?.Invoke(this, fileName);
                        return file;
                    }
                }
            }
            CurrentImagePath = file;
            CurrentImageName = fileName;
            ReceivedShowingPicture?.Invoke(this, fileName);
            return file;
        }

        if (FirmwareVersionFloat >= 2.0)
        {
            Trace.WriteLine("Trying to store in cache from busy tag serial");
            SendCommand($"AT+GF={fileName}\r\n");
            return "";
        }
        else
        {
            Trace.WriteLine("Trying to store in cache from busy tag drive");
            if (_busyTagDrive == null) _busyTagDrive = FindBusyTagDrive();
            if (_busyTagDrive == null) return "";
            var path = Path.Combine(_busyTagDrive.Name, fileName);
            File.Copy(path, file);
            CurrentImagePath = file;
            CurrentImageName = fileName;
            ReceivedShowingPicture?.Invoke(this, fileName);
            return file;
        }
    }

    public string GetFilePathFromCache(string fileName)
    {
        // if(string.IsNullOrEmpty(fileName)) return null;
        var file = Path.Combine(CachedImageDirPath, fileName);
        if (File.Exists(file))
        {
            return file;
        }

        return GetFile(fileName);
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

    public void SetUsbMassStorageActive(bool active)
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand($"AT+UMSA={(active ? 1 : 0)}\r\n");
    }

    public void ShowPicture(string fileName)
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand($"AT+SP={fileName}\r\n");
    }

    public void RestartDevice()
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand("AT+RST\r\n");
    }

    public void FormatDisk()
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand("AT+FD\r\n");
    }

    public void ActivateFileStorageScan()
    {
        // ReSharper disable once StringLiteralTypo
        SendCommand("AT+AFSS\r\n");
    }

    // ReSharper disable once InconsistentNaming
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
}