namespace BusyTag.Lib.Util;

public class UploadProgressArgs: EventArgs
{
    public string FileName = null!;
    public float ProgressLevel;

}