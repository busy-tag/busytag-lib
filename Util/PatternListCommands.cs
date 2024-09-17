namespace BusyTag.Lib.Util;

public class PatternListCommands
{
    public enum PatternName
    {
        GetDefault,
        GetPolice1,
        GetPolice2,
        GetRedFlashes,
        GetGreenFlashes,
        GetBlueFlashes,
        GetYellowFlashes,
        GetMagentaFlashes,
        GetCyanFlashes,
        GetWhiteFlashes,
        GetRedRunningLed,
        GetGreenRunningLed,
        GetBlueRunningLed,
        GetYellowRunningLed,
        GetMagentaRunningLed,
        GetCyanRunningLed,
        GetWhiteRunningLed,
        GetRedRunningWithOffLed,
        GetGreenRunningWithOffLed,
        GetBlueRunningWithOffLed,
        GetYellowRunningWithOffLed,
        GetMagentaRunningWithOffLed,
        GetCyanRunningWithOffLed,
        GetWhiteRunningWithOffLed,
    }

    public static readonly Dictionary<PatternName, PatternListItem?> PatternList = new()
    {
        {
            PatternName.GetDefault, new PatternListItem("Default",
            [
                new PatternLine(127, "1291AF", 100, 0),
                new PatternLine(127, "FF0000", 100, 0)
            ])
        },
        {
            PatternName.GetPolice1, new PatternListItem("Police1",
            [
                new PatternLine(120, "FF0000", 5, 50),
                new PatternLine(120, "000000", 5, 50),
                new PatternLine(15, "0000FF", 5, 50),
                new PatternLine(15, "000000", 5, 50)
            ])
        },
        {
            PatternName.GetPolice2, new PatternListItem("Police2",
            [
                new PatternLine(120, "FF0000", 3, 20),
                new PatternLine(120, "000000", 3, 20),
                new PatternLine(120, "FF0000", 3, 20),
                new PatternLine(120, "000000", 3, 20),
                new PatternLine(15, "0000FF", 3, 20),
                new PatternLine(15, "000000", 3, 20),
                new PatternLine(15, "0000FF", 3, 20),
                new PatternLine(15, "000000", 3, 20),
            ])
        },
        {
            PatternName.GetRedFlashes, new PatternListItem("Red flashes",
            [
                new PatternLine(127, "FF0000", 5, 10),
                new PatternLine(127, "000000", 5, 10),
            ])
        },
        {
            PatternName.GetGreenFlashes, new PatternListItem("Green flashes",
            [
                new PatternLine(127, "00FF00", 5, 10),
                new PatternLine(127, "000000", 5, 10),
            ])
        },
        {
            PatternName.GetBlueFlashes, new PatternListItem("Blue flashes",
            [
                new PatternLine(127, "0000FF", 5, 10),
                new PatternLine(127, "000000", 5, 10),
            ])
        },
        {
            PatternName.GetYellowFlashes, new PatternListItem("Yellow flashes",
            [
                new PatternLine(127, "FFFF00", 5, 10),
                new PatternLine(127, "000000", 5, 10),
            ])
        },
        {
            PatternName.GetCyanFlashes, new PatternListItem("Cyan flashes",
            [
                new PatternLine(127, "00FFFF", 5, 10),
                new PatternLine(127, "000000", 5, 10),
            ])
        },
        {
            PatternName.GetMagentaFlashes, new PatternListItem("Magenta flashes",
            [
                new PatternLine(127, "FF00FF", 5, 10),
                new PatternLine(127, "000000", 5, 10),
            ])
        },
        {
            PatternName.GetWhiteFlashes, new PatternListItem("White flashes",
            [
                new PatternLine(127, "FFFFFF", 5, 10),
                new PatternLine(127, "000000", 5, 10),
            ])
        },
        {
            PatternName.GetRedRunningLed, new PatternListItem("Red running led",
            [
                new PatternLine(129, "FF0000", 10, 0),
                new PatternLine(130, "FF0000", 10, 0),
                new PatternLine(132, "FF0000", 10, 0),
                new PatternLine(136, "FF0000", 10, 0),
                new PatternLine(144, "FF0000", 10, 0),
                new PatternLine(160, "FF0000", 10, 0),
                new PatternLine(192, "FF0000", 10, 0),
            ])
        },
        {
            PatternName.GetGreenRunningLed, new PatternListItem("Green running led",
            [
                new PatternLine(129, "00FF00", 10, 0),
                new PatternLine(130, "00FF00", 10, 0),
                new PatternLine(132, "00FF00", 10, 0),
                new PatternLine(136, "00FF00", 10, 0),
                new PatternLine(144, "00FF00", 10, 0),
                new PatternLine(160, "00FF00", 10, 0),
                new PatternLine(192, "00FF00", 10, 0),
            ])
        },
        {
            PatternName.GetBlueRunningLed, new PatternListItem("Blue running led",
            [
                new PatternLine(129, "0000FF", 10, 0),
                new PatternLine(130, "0000FF", 10, 0),
                new PatternLine(132, "0000FF", 10, 0),
                new PatternLine(136, "0000FF", 10, 0),
                new PatternLine(144, "0000FF", 10, 0),
                new PatternLine(160, "0000FF", 10, 0),
                new PatternLine(192, "0000FF", 10, 0),
            ])
        },
        {
            PatternName.GetYellowRunningLed, new PatternListItem("Yellow running led",
            [
                new PatternLine(129, "FFFF00", 10, 0),
                new PatternLine(130, "FFFF00", 10, 0),
                new PatternLine(132, "FFFF00", 10, 0),
                new PatternLine(136, "FFFF00", 10, 0),
                new PatternLine(144, "FFFF00", 10, 0),
                new PatternLine(160, "FFFF00", 10, 0),
                new PatternLine(192, "FFFF00", 10, 0),
            ])
        },
        {
            PatternName.GetCyanRunningLed, new PatternListItem("Cyan running led",
            [
                new PatternLine(129, "00FFFF", 10, 0),
                new PatternLine(130, "00FFFF", 10, 0),
                new PatternLine(132, "00FFFF", 10, 0),
                new PatternLine(136, "00FFFF", 10, 0),
                new PatternLine(144, "00FFFF", 10, 0),
                new PatternLine(160, "00FFFF", 10, 0),
                new PatternLine(192, "00FFFF", 10, 0),
            ])
        },
        {
            PatternName.GetMagentaRunningLed, new PatternListItem("Magenta running led",
            [
                new PatternLine(129, "FF00FF", 10, 0),
                new PatternLine(130, "FF00FF", 10, 0),
                new PatternLine(132, "FF00FF", 10, 0),
                new PatternLine(136, "FF00FF", 10, 0),
                new PatternLine(144, "FF00FF", 10, 0),
                new PatternLine(160, "FF00FF", 10, 0),
                new PatternLine(192, "FF00FF", 10, 0),
            ])
        },
        {
            PatternName.GetWhiteRunningLed, new PatternListItem("White running led",
            [
                new PatternLine(129, "FFFFFF", 10, 0),
                new PatternLine(130, "FFFFFF", 10, 0),
                new PatternLine(132, "FFFFFF", 10, 0),
                new PatternLine(136, "FFFFFF", 10, 0),
                new PatternLine(144, "FFFFFF", 10, 0),
                new PatternLine(160, "FFFFFF", 10, 0),
                new PatternLine(192, "FFFFFF", 10, 0),
            ])
        }
    };

    public PatternListItem? PatternListByKey(PatternName var)
    {
        return PatternList[var];
    }
    
    public static PatternListItem? PatternListByName(string var)
    {
        foreach (var item in PatternList)
        {
            if (var.Equals(item.Value!.Name))
            {
                return item.Value;
            }
            
        }

        return null;
    }
}