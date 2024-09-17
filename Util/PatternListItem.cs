namespace BusyTag.Lib.Util;

public class PatternListItem(string name, List<PatternLine> patternLines)
{
    public string Name { get; private set; } = name;
    public List<PatternLine> PatternLines { get; private set; } = patternLines;
}