// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text.Json;
using BusyTag.Lib.Util;
using BusyTag.Lib.Util.DevEventArgs;


namespace BusyTag.Lib;

// All the code in this file is included in all platforms.
public class BusyTagDevice(string? portName)
{
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<bool>? ReceivedDeviceBasicInformation;
    public event EventHandler<LedArgs>? ReceivedSolidColor;
    public event EventHandler<List<PatternLine>>? ReceivedPattern;
    public event EventHandler<bool>? ReceivedShowAfterDrop;
    public event EventHandler<bool>? ReceivedAllowedWebServer;
    public event EventHandler<WifiConfigArgs>? ReceivedWifiConfig;
    public event EventHandler<bool>? ReceivedUsbMassStorageActive;
    public event EventHandler<string>? ReceivedShowingPicture;
    public event EventHandler<int>? ReceivedDisplayBrightness;
    public event EventHandler<List<FileStruct>>? FileListUpdated;
    public event EventHandler<bool>? FileUploadFinished;
    public event EventHandler<UploadProgressArgs>? FileUploadProgress;
    public event EventHandler<string>? ShowingNewPicture;
    public event EventHandler<float>? FirmwareUpdateStatus;
    public event EventHandler<bool>? PlayPatternStatus;
    public event EventHandler<bool>? WritingInStorage;
    private SerialPort? _serialPort;
    private DeviceConfig _deviceConfig = new();
    public string? PortName { get; } = portName;

    public bool Connected => _serialPort is { IsOpen: true };
    public string DeviceName { get; private set; } = null!;
    public string ManufactureName { get; private set; } = null!;
    public string Id { get; private set; } = null!;

    public string FirmwareVersion { get; private set; } = null!;
    public float FirmwareVersionFloat { get; private set; }
    public string CurrentImageName { get; private set; } = null!;

    // public FileStruct[] PictureList { get; private set; }
    // public FileStruct[] FileList { get; private set; }
    public string LocalHostAddress { get; private set; } = null!;

    // public long FreeStorageSize { get; private set; }
    // public long TotalStorageSize { get; private set; }
    private DriveInfo? _busyTagDrive;
    private readonly List<PatternLine> _patternList = [];

    private static readonly SerialPortCommands Commands = new();
    private SerialPortCommands.Commands _currentCommand = SerialPortCommands.Commands.GetFirmwareVersion;
    private bool _gotAllBasicInfo;
    private bool _gotDriveInfo;
    private bool _receivingPattern;
    private bool _sendingNewPattern;
    private bool _isPlayingPattern = false;
    private bool _playPatternAfterSending = false;
    private readonly CancellationTokenSource _ctsForConnection = new();
    private CancellationTokenSource _ctsForFileSending = new();

    private void ConnectionTask(int milliseconds, CancellationToken token)
    {
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), token);
                if (!Connected)
                {
                    Disconnect();
                }
#if MACCATALYST
                try
                {
                    // Send a simple command to check if the device is responsive
                    _serialPort?.WriteLine("AT");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Exception in connection check: {ex.Message}");
                    Disconnect();
                }
#endif
            }
        }, token);
    }


    public void Connect()
    {
        _serialPort = new SerialPort(PortName, 460800, Parity.None, 8, StopBits.One);
        _serialPort.ReadTimeout = 500;
        _serialPort.WriteTimeout = 500;
        _serialPort.DataReceived += sp_DataReceived;
        _serialPort.ErrorReceived += sp_ErrorReceived;

        _serialPort.Open();
        ConnectionStateChanged?.Invoke(this, Connected);
        if (!Connected)
        {
            Disconnect();
            return;
        }

        _gotAllBasicInfo = false;
        _currentCommand = SerialPortCommands.Commands.GetDeviceName;
        SendCommand(Commands.GetCommand(_currentCommand));
        ConnectionTask(1000, _ctsForConnection.Token);
    }

    public void Disconnect()
    {
        if (_serialPort == null) return; // TODO Possibly change to exception

        // if (Connected)
        // {
        _serialPort.Close();
        _serialPort.Dispose();
        // }

        ConnectionStateChanged?.Invoke(this, false);
        _ctsForConnection.Cancel();
        _serialPort = null;
    }

    public SerialPort? SerialPort()
    {
        return _serialPort;
    }

    private bool SendCommand(string data)
    {
        if (_serialPort == null) return false; // TODO Possibly change to exception

        if (!Connected) return false;

        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]TX:{data}");
        try
        {
            _serialPort.WriteLine(data);
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
        }
        return true;
    }

    private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null) return; // TODO Possibly change to exception

        // string data = _serialPort.ReadLine();
        const int bufSize = 512;
        var buf = new byte[bufSize];
        // string data = _serialPort.ReadLine();
        // ReSharper disable once UnusedVariable
        var len = _serialPort.Read(buf, 0, bufSize);
        // long timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        // Trace.WriteLine($"timestamp: {timestamp}, len:{len}");
        var data = System.Text.Encoding.UTF8.GetString(buf, 0, buf.Length);

        FilterResponse(data);
    }

    private void sp_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
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
                    }
                    // else if (parts[0].Equals("+LHA"))
                    // {
                    //     LocalHostAddress = parts[1].Trim();
                    // }
                    else if (parts[0].Equals("+FSS"))
                    {
                        // FreeStorageSize = long.Parse(parts[1]);
                    }
                    else if (parts[0].Equals("+TSS"))
                    {
                        // TotalStorageSize = long.Parse(parts[1]);
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
                                SSID = args[0].Trim(),
                                Pass = args[1].Trim()
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
                        ReceivedShowingPicture?.Invoke(this, parts[1].Trim());
                    }
                    else if (parts[0].Equals("+evn"))
                    {
                        var args = parts[1].Split(',');
                        if (args.Length >= 2)
                        {
                            if (args[0].Equals("SP"))
                            {
                                CurrentImageName = args[1].Trim();
                                ShowingNewPicture?.Invoke(this, CurrentImageName);
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
                            PlayPattern(true, 5);
                        }
                    }
                }
                else if (line.Equals(">") && _sendingNewPattern)
                {
                    foreach (var item in _patternList)
                    {
                        SendCommand($"+CP:{item.ledBits},{item.color},{item.speed},{item.delay}\r\n");
                    }

                    _sendingNewPattern = false;
                    if (_playPatternAfterSending)
                    {
                        PlayPattern(true, 5);
                    }
                }
            }
        }

        if (!_gotAllBasicInfo)
        {
            _currentCommand++;
            if (_currentCommand > SerialPortCommands.Commands.SetShowAfterDrop)
            {
                if (!_gotDriveInfo)
                {
                    if (FirmwareVersionFloat > 0.7)
                    {
                        SetAllowedAutoStorageScan(false);
                    }

                    _gotDriveInfo = true;
                    _busyTagDrive = FindBusyTagDrive();
                    TryToGetFileList();
                }

                _gotAllBasicInfo = true;
                ReceivedDeviceBasicInformation?.Invoke(this, _gotAllBasicInfo);
            }
            else
            {
                SendCommand(Commands.GetCommand(_currentCommand));
            }
        }
    }

    // public void GetPictureList()
    // {
    //     SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetPictureList));
    // }
    //
    // public void GetFileList()
    // {
    //     SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetFileList));
    // }

    public long FreeStorageSize()
    {
        return _busyTagDrive?.TotalFreeSpace ?? 0;
    }

    public long TotalStorageSize()
    {
        return _busyTagDrive?.TotalSize ?? 0;
    }

    // ReSharper disable once InconsistentNaming
    public void GetSolidColor()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetSolidColor));
    }

    // ReSharper disable once InconsistentNaming
    public void GetCustomPattern()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetCustomPattern));
        _patternList.Clear();
    }

    // ReSharper disable once InconsistentNaming
    public void GetDisplayBrightness()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetDisplayBrightness));
    }

    public void GetShowingPicture()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetShowingPicture));
    }

    // ReSharper disable once InconsistentNaming
    public void GetShowAfterDrop()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetShowAfterDrop));
    }

    // ReSharper disable once InconsistentNaming
    public void GetAllowedWebServer()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetAllowedWebServer));
    }

    // ReSharper disable once InconsistentNaming
    public void GetWifiConfig()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetWifiConfig));
    }

    // ReSharper disable once InconsistentNaming
    public void GetUsbMassStorageActive()
    {
        SendCommand(Commands.GetCommand(SerialPortCommands.Commands.GetUsbMassStorageActive));
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
        // ReSharper disable once StringLiteralTypo
        SendCommand($"AT+SC={ledBits:d},{red:X2}{green:X2}{blue:X2}\r\n");
        var eventArgs = new LedArgs
        {
            LedBits = ledBits,
            Color = $"{red:X2}{green:X2}{blue:X2}"
        };
        ReceivedSolidColor?.Invoke(this, eventArgs);
    }

    public void SetNewCustomPattern(List<PatternLine> list, bool playAfterSending)
    {
        if (_isPlayingPattern)
        {
            PlayPattern(false, 3);
            if (FirmwareVersionFloat < 1.1)
                Thread.Sleep(200);
        }

        _playPatternAfterSending = playAfterSending;
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
            _deviceConfig.activatePattern = false;
            _deviceConfig.customPatternArr = _patternList;

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
        var allDrives = DriveInfo.GetDrives();
        foreach (var d in allDrives)
        {
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
        if (_busyTagDrive == null) return;

        _ctsForFileSending = new CancellationTokenSource();
        Task.Run(() =>
        {
            //TODO: need to recheck if sourcePath and destPath is not the same
            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(_busyTagDrive.Name, fileName);
            if (!FreeUpStorage(new FileInfo(sourcePath).Length))
            {
                _ctsForFileSending.Cancel();
            }
            
            File.Copy(sourcePath, destPath, true);
            FileUploadFinished?.Invoke(this, true);

            _ctsForFileSending.Cancel();
            return Task.CompletedTask;
        }, _ctsForFileSending.Token);
    }

    public void SendNewFileWithProgressEvents(string sourcePath)
    {
        if (_busyTagDrive == null) return;

        _ctsForFileSending = new CancellationTokenSource();
        Task.Run(() =>
        {
            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(_busyTagDrive.Name, fileName);
            var args = new UploadProgressArgs
            {
                FileName = fileName,
                ProgressLevel = 0.0f
            };

            if (!FreeUpStorage(new FileInfo(sourcePath).Length))
            {
                _ctsForFileSending.Cancel();
                return;
            }

            using var fsOut = new FileStream(destPath, FileMode.Create);
            using var fsIn = new FileStream(sourcePath, FileMode.Open);

            if (fsOut == null || fsIn == null)
            {
                _ctsForFileSending.Cancel();
                return;
            }

            var bt = new byte[4096];
            int readByte;

            while ((readByte = fsIn.Read(bt, 0, bt.Length)) > 0 && _ctsForFileSending.Token.IsCancellationRequested == false)
            {
                try
                {
                    fsOut.Write(bt, 0, readByte);
                    args.ProgressLevel = (float)(fsIn.Position * 100.0 / fsIn.Length);
                    FileUploadProgress?.Invoke(this, args);
                }
                catch (Exception)
                {
                    DeleteFile(fileName);
                    FileUploadFinished?.Invoke(this, false);
                    _ctsForFileSending.Cancel();
                }
            }

            FileUploadFinished?.Invoke(this, true);

            _ctsForFileSending.Cancel();
        }, _ctsForFileSending.Token);
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

        if (files.Any())
        {
            // Delete the oldest file
            var oldestFile = files.First();
            oldestFile.Delete();
            Trace.WriteLine($"Deleted oldest file: {oldestFile.Name}");
        }
    }

    public bool FreeUpStorage(long size)
    {
        if (_busyTagDrive == null) return false;
        var counter = 0;
        while (FreeStorageSize() < size)
        {
            DeleteOldestFileInDirectory(_busyTagDrive.Name);
            counter++;
            if (counter < 20) continue;
            return false;
        }
        return true;
    }

    public void TryToGetFileList()
    {
        var fileNames = new List<FileStruct>();
        if (_busyTagDrive != null)
        {
            var di = new DirectoryInfo(_busyTagDrive.Name);
            // ReSharper disable once LoopCanBeConvertedToQuery
            try
            {
                var files = di.GetFiles();
                foreach (var fi in files)
                {
                    fileNames.Add(new FileStruct(fi.Name, fi.Length));
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine(e.Message);
            }

        }

        FileListUpdated?.Invoke(this, fileNames);
    }

    public void DeleteFile(string fileName)
    {
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

    // ReSharper disable once InconsistentNaming
    public MemoryStream GetImage(string fileName)
    {
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
        if (_busyTagDrive == null) return false;
        var path = Path.Combine(_busyTagDrive.Name, fileName);
        return File.Exists(path);
    }

    public string GetFullFilePath(string fileName)
    {
        if (_busyTagDrive == null) return string.Empty;
        var path = Path.Combine(_busyTagDrive.Name, fileName);
        return path;
    }

    public FileInfo? GetFileInfo(string fileName)
    {
        if (_busyTagDrive == null) return null;
        var path = Path.Combine(_busyTagDrive.Name, fileName);
        var fileInfo = new FileInfo(path);
        return fileInfo;
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
            showAfterDrop = false,
            allowUsbMsc = true,
            allowFileServer = false,
            dispBrightness = 100,
            solidColor = new DeviceConfig.SolidColor(127, "990000"),
            activatePattern = false,
            patternRepeat = 3
        };
        var lines = new List<PatternLine>
        {
            new(127, "1291AF", 100, 0),
            new(127, "FF0000", 100, 0)
        };
        _deviceConfig.customPatternArr = lines;
    }
}