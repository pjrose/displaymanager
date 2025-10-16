using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using DisplayManagerLib;
using DisplayLib = DisplayManagerLib.DisplayManager;

namespace DisplayManager.WpfApp;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FriendlyNameStore _friendlyNameStore = new();
    private readonly WindowPlacementTracker _placementTracker = new();

    public ObservableCollection<MonitorViewModel> Monitors { get; } = new();

    private MonitorViewModel? _selectedMonitor;
    public MonitorViewModel? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!Equals(_selectedMonitor, value))
            {
                _selectedMonitor = value;
                OnPropertyChanged();
            }
        }
    }

    private string _statusMessage = "Load monitors to begin.";
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (!Equals(_statusMessage, value))
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        try
        {
            _friendlyNameStore.Load();
            StatusMessage = $"Loaded {_friendlyNameStore.Assignments.Count} stored friendly names.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to load saved friendly names.";
            MessageBox.Show(this, ex.Message, "Friendly Name Store", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshMonitors();

        DisplayLib.HookDisplayChanges(this, () =>
        {
            RefreshMonitors();
            _placementTracker.RestoreTrackedWindows();
        });
    }

    private void RefreshMonitors()
    {
        try
        {
            var monitors = DisplayLib.GetMonitors();
            Monitors.Clear();
            foreach (var monitor in monitors)
            {
                var assigned = _friendlyNameStore.GetFriendlyName(monitor);
                Monitors.Add(new MonitorViewModel(monitor, assigned));
            }

            if (Monitors.Count > 0)
            {
                SelectedMonitor ??= Monitors[0];
                StatusMessage = $"Loaded {Monitors.Count} monitor(s).";
            }
            else
            {
                StatusMessage = "No active monitors were reported by the system.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Unable to enumerate monitors.";
            MessageBox.Show(this, ex.ToString(), "Enumeration Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitors();

    private void AssignFriendlyName_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMonitor is null)
        {
            MessageBox.Show(this, "Select a monitor from the list first.", "No Monitor Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new MonitorFriendlyNameDialog(SelectedMonitor)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _friendlyNameStore.SetFriendlyName(SelectedMonitor.MonitorKey, dialog.FriendlyName);
            try
            {
                _friendlyNameStore.Save();
                SelectedMonitor.AssignedName = dialog.FriendlyName;
                StatusMessage = string.IsNullOrWhiteSpace(dialog.FriendlyName)
                    ? $"Cleared stored friendly name for {SelectedMonitor.Info.DeviceName}."
                    : $"Stored '{dialog.FriendlyName}' for {SelectedMonitor.Info.DeviceName}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void LocateMonitor_Click(object sender, RoutedEventArgs e)
    {
        var monitor = PromptForMonitorByFriendlyName(out var friendlyName);
        if (monitor is null)
            return;

        var message = $"Resolved '{friendlyName}' to:\n\n{monitor.GetVerboseDescription()}";
        MessageBox.Show(this, message, "Monitor Located", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RotateMonitor_Click(object sender, RoutedEventArgs e)
    {
        var monitor = PromptForMonitorByFriendlyName(out var friendlyName);
        if (monitor is null)
            return;

        var orientationDialog = new OrientationDialog { Owner = this };
        if (orientationDialog.ShowDialog() != true)
            return;

        try
        {
            DisplayLib.Rotate(monitor.Info, orientationDialog.SelectedOrientation);
            StatusMessage = $"Requested rotation of '{friendlyName}' to {orientationDialog.SelectedOrientation}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Rotation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowTestWindow_Click(object sender, RoutedEventArgs e)
    {
        var monitor = PromptForMonitorByFriendlyName(out var friendlyName);
        if (monitor is null)
            return;

        var testWindow = new TestWindow { Owner = this };
        testWindow.Show();
        DisplayLib.ApplyWindow(testWindow, monitor.Info, maximize: false, widthDip: 400, heightDip: 240, marginDip: 30, tracker: _placementTracker);
        StatusMessage = $"Moved test window to '{friendlyName}'.";
    }

    private void ShowTrackedWindows_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TrackedWindowsDialog(_placementTracker, Monitors)
        {
            Owner = this
        };

        dialog.ShowDialog();
    }

    private MonitorViewModel? PromptForMonitorByFriendlyName(out string friendlyName)
    {
        friendlyName = string.Empty;
        var prompt = new FriendlyNamePromptDialog(_friendlyNameStore.KnownFriendlyNames())
        {
            Owner = this
        };

        if (prompt.ShowDialog() != true)
            return null;

        friendlyName = prompt.SelectedName.Trim();
        if (string.IsNullOrWhiteSpace(friendlyName))
            return null;

        var nameToFind = friendlyName;

        if (_friendlyNameStore.TryGetMonitorKey(nameToFind, out var key))
        {
            var matched = Monitors.FirstOrDefault(m => string.Equals(m.MonitorKey, key, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
                return matched;
        }

        var fallback = Monitors.FirstOrDefault(m => m.DisplayNameMatches(nameToFind));
        if (fallback != null)
            return fallback;

        MessageBox.Show(this, $"No active monitor matched '{friendlyName}'. Please choose an alternate.", "Monitor Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);

        var picker = new MonitorPickerDialog(Monitors)
        {
            Owner = this
        };

        return picker.ShowDialog() == true ? picker.SelectedMonitor : null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
