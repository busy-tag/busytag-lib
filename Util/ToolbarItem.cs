namespace BusyTag.Lib.Util;

/// <summary>One entry of the device's top-toolbar layout (AT+TBI). Firmware v3.0+.</summary>
public readonly struct ToolbarItem
{
    public int Id { get; init; }
    public bool Enabled { get; init; }

    /// <summary>Which side the item sits on: 0 = left, 1 = right.</summary>
    public int Side { get; init; }
}
