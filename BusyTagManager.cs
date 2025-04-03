using System.Diagnostics;
using System.IO.Ports;
using BusyTag.Lib.Util;

namespace BusyTag.Lib;

public class BusyTagManager
{
    public event EventHandler<Dictionary<string, bool>>? FoundSerialDevices;
    public event EventHandler<List<string>>? FoundBusyTagSerialDevices;
    private Dictionary<string, bool> _serialDeviceList = new();
    private SerialPort? _serialPort;
    private static bool _isScanningForDevices = false;

    public string[] AllSerialPorts()
    {
        return SerialPort.GetPortNames();
    }

    public void FindBusyTagDevice()
    {
        // Trace.WriteLine($"FindBusyTagDevice(), _isScanningForDevices: {_isScanningForDevices}");
        if (_isScanningForDevices) return;
        _isScanningForDevices = true;

        Task.Run(async () =>
        {
            string[] ports = SerialPort.GetPortNames();
            // Trace.WriteLine($"ports: {string.Join(", ", ports)}");
            _serialDeviceList = new Dictionary<string, bool>();

            foreach (var port in ports)
            {
#if MACCATALYST
                if (!_serialDeviceList.ContainsKey(port) &&
                    port.StartsWith("/dev/tty.usbmodem", StringComparison.Ordinal))
#else
                if (!_serialDeviceList.ContainsKey(port))
#endif
                {
                    _serialDeviceList[port] = false;
                }
            }

            foreach (var port in _serialDeviceList.Keys)
            {
                // Trace.WriteLine($"Port: {port}");
                // _serialDeviceList.Add(port, false);
                if (_serialPort != null && _serialPort.IsOpen)
                    _serialPort.Close();

                _serialPort = new SerialPort(port, 460800, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    WriteBufferSize = 8192,
                    ReadBufferSize = 8192
                };

                _serialPort.DataReceived += sp_DataReceived;
                _serialPort.ErrorReceived += sp_ErrorReceived;

                var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(1)); // Set timeout to 1 second

                try
                {
                    var portOpened = await OpenSerialPortWithTimeoutAsync(_serialPort, cts.Token);
                    if (portOpened && _serialPort.IsOpen)
                    {
                        SendCommand(new SerialPortCommands().GetCommand(SerialPortCommands.Commands.GetDeviceName));
                        await Task.Delay(150, cts.Token); // Wait for 150 ms to receive data
                        _serialPort.Close();
                    }
                }
                catch (OperationCanceledException)
                {
                    Trace.WriteLine($"Timeout opening port: {port}");
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"Error: {e.Message}");
                }
            }

            var busyTagPortList = new List<string>();
            foreach (var item in _serialDeviceList)
            {
                if (item.Value)
                {
                    busyTagPortList.Add(item.Key);
                }
            }

            _isScanningForDevices = false;

            SafeInvokeFoundSerialDevices(_serialDeviceList);
            SafeInvokeFoundBusyTagSerialDevices(busyTagPortList);
        });
    }

    private void SafeInvokeFoundSerialDevices(Dictionary<string, bool> devices)
    {
        if (FoundSerialDevices == null) return;

        try
        {
            FoundSerialDevices.Invoke(this, devices);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error in FoundSerialDevices event handler: {ex.Message}");
        }
    }

    private void SafeInvokeFoundBusyTagSerialDevices(List<string> devices)
    {
        if (FoundBusyTagSerialDevices == null) return;

        try
        {
            FoundBusyTagSerialDevices.Invoke(this, devices);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error in FoundBusyTagSerialDevices event handler: {ex.Message}");
        }
    }

    private static async Task<bool> OpenSerialPortWithTimeoutAsync(SerialPort serialPort, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>();

        var thread = new Thread(() =>
        {
            try
            {
                serialPort.Open();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.Start();

        using (token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException operationCanceledException)
            {
                Trace.WriteLine($"Error: {operationCanceledException.Message}");
                // The thread will exit naturally since it checks the cancellation token
                throw;
            }
            catch
            {
                Trace.WriteLine($"Error");
                // The thread will handle exceptions and exit naturally
                throw;
            }
        }
    }

    private void SendCommand(string data)
    {
        if (_serialPort is not { IsOpen: true }) return;

        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]TX:{data}");
        _serialPort.WriteLine(data);
    }

    private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return;

        try
        {
            const int bufSize = 512;
            var buf = new byte[bufSize];
            var len = _serialPort.Read(buf, 0, bufSize);
            var data = System.Text.Encoding.UTF8.GetString(buf, 0, len);
            var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{data}");

            if (data.Contains("+DN:busytag-") || data.Contains("+evn:"))
            {
                _serialDeviceList[_serialPort.PortName] = true;
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("port is closed"))
        {
            Trace.WriteLine("Port closed during read operation");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error in DataReceived: {ex.Message}");
        }
    }

    private static void sp_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        Trace.WriteLine(e.ToString());
    }

    private static string UnixToDate(long timestamp, string convertFormat)
    {
        var convertedUnixTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
        return convertedUnixTime.ToString(convertFormat);
    }
}