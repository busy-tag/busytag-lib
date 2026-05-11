namespace BusyTag.Lib.Util;

public class WifiAccessPoint
{
    public string Ssid { get; init; } = string.Empty;
    public int Channel { get; init; }
    public int Rssi { get; init; }
    public string AuthMode { get; init; } = string.Empty;

    public bool IsOpen =>
        string.Equals(AuthMode, "OPEN", System.StringComparison.OrdinalIgnoreCase);
}
