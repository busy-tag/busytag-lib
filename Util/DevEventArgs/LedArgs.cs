namespace BusyTag.Lib.Util.DevEventArgs;

public class LedArgs : EventArgs
{
    public int LedBits;
    public string Color = null!;
}