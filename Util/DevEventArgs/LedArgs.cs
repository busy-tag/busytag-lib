namespace BusyTag.Lib.Util.DevEventArgs;

public class LedArgs : EventArgs
{
    public int LedBits { get; set; }
    public string Color { get; set; } = string.Empty;
}