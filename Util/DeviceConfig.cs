using System.Text.Json.Serialization;

namespace BusyTag.Lib.Util;

public class DeviceConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; }
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("show_after_drop")]
    public bool showAfterDrop { get; set; }
    [JsonPropertyName("allow_usb_msc")]
    public bool allowUsbMsc { get; set; }
    [JsonPropertyName("allow_file_server")]
    public bool allowFileServer { get; set; }
    // ReSharper disable once StringLiteralTypo
    [JsonPropertyName("disp_brightness")]
    // ReSharper disable once IdentifierTypo
    public int dispBrightness { get; set; }
    [JsonPropertyName("solid_color")]
    public SolidColor solidColor { get; set; } = null!;

    [JsonPropertyName("activate_pattern")]
    public bool activatePattern { get; set; }
    [JsonPropertyName("pattern_repeat")]
    public int patternRepeat { get; set; }
    [JsonPropertyName("custom_pattern_arr")]
    public List<PatternLine> customPatternArr { get; set; } = null!;

    public class SolidColor(int ledBits, string color)
    {
        [JsonPropertyName("led_bits")]
        public int ledBits { get; set; } = ledBits;
        [JsonPropertyName("color")]
        public string Color { get; set; } = color;
    }
    
}