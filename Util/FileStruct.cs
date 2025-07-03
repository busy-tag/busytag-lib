namespace BusyTag.Lib.Util;

public struct FileStruct(string name, long size)
{
    
    public enum Type
    {
        
        File,
        Directory
    }

    public string Name { get; set; } = name;
    public long Size { get; set; } = size;
    public Type FileType { get; set; } = Type.File;

    public override string ToString()
    {
        return $"name:{Name},size:{Size}";
    }
}