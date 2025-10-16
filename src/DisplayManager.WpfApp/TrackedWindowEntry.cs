using System;
using System.Collections.Generic;
using System.Globalization;
using DisplayManagerLib;

namespace DisplayManager.WpfApp;

public class TrackedWindowEntry
{
    private readonly TrackedWindowInfo _info;

    public TrackedWindowEntry(TrackedWindowInfo info)
    {
        _info = info;
    }

    public Guid Id => _info.Id;

    public string WindowName => _info.WindowName;

    public string FriendlyName => string.IsNullOrWhiteSpace(_info.LastKnownFriendlyName)
        ? "(not set)"
        : _info.LastKnownFriendlyName;

    public long AdapterLuid => _info.AdapterLuid;

    public string AdapterLuidHex => $"0x{AdapterLuid:X}";

    public uint TargetId => _info.TargetId;

    public WindowPlacementOptions Options => _info.Options;

    public string PlacementDescription => BuildPlacementDescription(_info.Options);

    private static string BuildPlacementDescription(WindowPlacementOptions options)
    {
        var parts = new List<string>();
        parts.Add(options.Maximize ? "Maximized" : "Normal");

        if (options.WidthDip.HasValue || options.HeightDip.HasValue)
        {
            var width = options.WidthDip.HasValue
                ? options.WidthDip.Value.ToString("0.#", CultureInfo.InvariantCulture)
                : "auto";
            var height = options.HeightDip.HasValue
                ? options.HeightDip.Value.ToString("0.#", CultureInfo.InvariantCulture)
                : "auto";
            parts.Add($"Size {width} x {height}");
        }

        if (options.MarginDip > 0)
        {
            parts.Add($"Margin {options.MarginDip.ToString("0.#", CultureInfo.InvariantCulture)}");
        }

        return string.Join(", ", parts);
    }
}
