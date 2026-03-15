namespace BusyTag.Lib.Util;

public enum BusyTagDeviceMode
{
    Normal,     // VID 303A, PID 81DF - running application firmware
    BootLoader  // VID 303A, PID 1001 - ESP32-S3 USB download mode (bricked/recovery)
}

public class BusyTagDeviceInfo
{
    public string PortName { get; init; } = string.Empty;
    public BusyTagDeviceMode Mode { get; init; }
    public string Vid { get; init; } = string.Empty;
    public string Pid { get; init; } = string.Empty;

    public override string ToString() =>
        $"{PortName} [{Mode}]";
}
