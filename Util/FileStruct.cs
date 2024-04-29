namespace BusyTag.Lib.Util;

public struct FileStruct
{
    
    public enum Type
    {
        File,
        Directory
    }

    public string Name { get; set; }
    public int Size { get; set; }
    public Type FileType { get; set; }

    public override string ToString()
    {
        return $"name:{Name},size:{Size}";
    }
}