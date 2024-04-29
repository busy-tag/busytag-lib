using System.Collections;
using System.Text.Json.Serialization;

namespace BusyTagLib.Util;

public class DeviceConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; }
    [JsonPropertyName("image")]
    public string Image { get; set; }
    [JsonPropertyName("show_after_drop")]
    public bool showAfterDrop { get; set; }
    [JsonPropertyName("allow_usb_msc")]
    public bool allowUsbMsc { get; set; }
    [JsonPropertyName("allow_file_server")]
    public bool allowFileServer { get; set; }
    [JsonPropertyName("disp_brightness")]
    public int dispBrightness { get; set; }
    [JsonPropertyName("solid_color")]
    public SolidColor solidColor { get; set; }
    [JsonPropertyName("activate_pattern")]
    public bool activatePattern { get; set; }
    [JsonPropertyName("pattern_repeat")]
    public int patternRepeat { get; set; }
    [JsonPropertyName("custom_pattern_arr")]
    public List<PatternLine> customPatternArr { get; set; }
    
    public class SolidColor(int ledBits, string color)
    {
        [JsonPropertyName("led_bits")]
        public int ledBits { get; set; } = ledBits;
        [JsonPropertyName("color")]
        public string Color { get; set; } = color;
    }
    
}