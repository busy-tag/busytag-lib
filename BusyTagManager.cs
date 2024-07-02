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
        if (_isScanningForDevices) return;
        _isScanningForDevices = true;
        var ctsForConnection = new CancellationTokenSource();
        Task.Run(() =>
        {
            string[] ports = SerialPort.GetPortNames();
            _serialDeviceList = new Dictionary<string, bool>();
            // Display each port name to the console.
            foreach (var port in ports)
            {
                // Trace.WriteLine(port);
                _serialDeviceList[port] = false;
                if (_serialPort is { IsOpen: true }) _serialPort.Close();
                _serialPort = new SerialPort(port, 460800, Parity.None, 8, StopBits.One);
                _serialPort.ReadTimeout = 500;
                _serialPort.WriteTimeout = 500;
                _serialPort.WriteBufferSize = 8192;
                _serialPort.ReadBufferSize = 8192;

                _serialPort.DataReceived += sp_DataReceived;
                _serialPort.ErrorReceived += sp_ErrorReceived;
                try
                {
                    _serialPort.Open();
                    if (_serialPort.IsOpen)
                    {
                        SendCommand(new SerialPortCommands().GetCommand(SerialPortCommands.Commands.GetDeviceName));
                        Thread.Sleep(100);
                        _serialPort.Close();
                    }
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"Error: {e.Message}");
                }
            }

            var busyTagPortList = new List<string>();
            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
            foreach (var item in _serialDeviceList)
            {
                if (item.Value)
                {
                    busyTagPortList.Add(item.Key);
                }
            }

            FoundSerialDevices?.Invoke(this, _serialDeviceList);
            FoundBusyTagSerialDevices?.Invoke(this, busyTagPortList);
            _isScanningForDevices = false;
            ctsForConnection.Cancel(); // Was CancelAsync
            return Task.CompletedTask;
        }, ctsForConnection.Token);
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
        if (_serialPort == null) return; // TODO Possibly change to exception

        var port = (SerialPort)sender;
        const int bufSize = 32;
        var buf = new byte[bufSize];
        // ReSharper disable once UnusedVariable
        var len = _serialPort.Read(buf, 0, bufSize);
        var data = System.Text.Encoding.UTF8.GetString(buf, 0, buf.Length);
        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{data}");

        // ReSharper disable once StringLiteralTypo
        if (data.Contains("+DN:busytag-"))
        {
            _serialDeviceList[port.PortName] = true;
        }
    }

    private void sp_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        Trace.WriteLine(e.ToString());
    }

    private static string UnixToDate(long timestamp, string convertFormat)
    {
        var convertedUnixTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
        return convertedUnixTime.ToString(convertFormat);
    }
}