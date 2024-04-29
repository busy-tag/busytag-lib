using System.Text.Json.Serialization;

namespace BusyTag.Lib.Util;

public class PatternLine(int ledBits, string color, int speed, int delay)
{
    [JsonPropertyName("led_bits")]
    public int ledBits { get; set; } = ledBits;
    [JsonPropertyName("color")]
    public string color{ get; set; } = color;
    [JsonPropertyName("speed")]
    public int speed{ get; set; } = speed;
    [JsonPropertyName("delay")]
    public int delay{ get; set; } = delay;
}