using System.Text.Json.Serialization;

namespace BusyTag.Lib.Util;

public class PatternLine(int ledBits, string color, int speed, int delay)
{
    [JsonPropertyName("led_bits")]
    public int LedBits { get; set; } = ledBits;
    [JsonPropertyName("color")]
    public string Color{ get; set; } = color;
    [JsonPropertyName("speed")]
    public int Speed{ get; set; } = speed;
    [JsonPropertyName("delay")]
    public int Delay{ get; set; } = delay;
}