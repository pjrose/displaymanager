using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DisplayManagerLib;
using DisplayLib = DisplayManagerLib.DisplayManager;

namespace DisplayManager.WpfApp;

public class MonitorViewModel : INotifyPropertyChanged
{
    private string? _assignedName;

    public MonitorViewModel(DisplayLib.MonitorInfo info, string? assignedName)
    {
        Info = info;
        MonitorKey = FriendlyNameStore.CreateMonitorKey(info);
        _assignedName = assignedName;
    }

    public DisplayLib.MonitorInfo Info { get; }

    public string MonitorKey { get; }

    public string? AssignedName
    {
        get => _assignedName;
        set
        {
            if (!string.Equals(_assignedName, value, StringComparison.Ordinal))
            {
                _assignedName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(AssignedName)
        ? Info.FriendlyName
        : AssignedName!;

    public string BoundsDescription => $"{Info.BoundsPx.Left},{Info.BoundsPx.Top} {Info.BoundsPx.Width}x{Info.BoundsPx.Height}";

    public string GetVerboseDescription()
    {
        return $"Assigned: {AssignedName ?? "(not set)"}\n" +
               $"System: {Info.FriendlyName}\n" +
               $"GDI: {Info.DeviceName}\n" +
               $"Device Path: {Info.DevicePath}\n" +
               $"Adapter LUID: 0x{Info.AdapterLuid:X}\n" +
               $"Target Id: {Info.TargetId}\n" +
               $"Bounds: {BoundsDescription}\n" +
               $"Primary: {Info.IsPrimary}";
    }

    public bool DisplayNameMatches(string friendlyName)
        => string.Equals(DisplayName, friendlyName, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Info.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
