namespace BusyTag.Lib.Util.DevEventArgs;

public class WifiConfigArgs: EventArgs
{
    public string Ssid { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}