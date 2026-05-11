namespace BusyTag.Lib.Util;

public class WifiStationStatus
{
    public bool Enabled { get; init; }
    public bool Connected { get; init; }
    public string Ssid { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Rssi { get; init; }

    public static WifiStationStatus Disabled() => new() { Enabled = false };
}
