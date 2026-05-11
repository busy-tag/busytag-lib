namespace BusyTag.Lib.Util.DevEventArgs;

public class WifiConnectionEventArgs : EventArgs
{
    public bool Connected { get; init; }
    public string Ssid { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    /// <summary>Reason code emitted on WIFI_CONNECT_FAILED. 0 when connected.</summary>
    public int ReasonCode { get; init; }
}
