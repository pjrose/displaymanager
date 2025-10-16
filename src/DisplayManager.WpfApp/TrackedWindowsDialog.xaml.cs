using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using DisplayManagerLib;
using DisplayLib = DisplayManagerLib.DisplayManager;

namespace DisplayManager.WpfApp;

public partial class TrackedWindowsDialog : Window, INotifyPropertyChanged
{
    private readonly WindowPlacementTracker _tracker;

    public ObservableCollection<TrackedWindowEntry> TrackedWindows { get; } = new();

    public IList<MonitorViewModel> Monitors { get; }

    private TrackedWindowEntry? _selectedTrackedWindow;
    public TrackedWindowEntry? SelectedTrackedWindow
    {
        get => _selectedTrackedWindow;
        set
        {
            if (!Equals(_selectedTrackedWindow, value))
            {
                _selectedTrackedWindow = value;
                OnPropertyChanged();
            }
        }
    }

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

    public TrackedWindowsDialog(WindowPlacementTracker tracker, IEnumerable<MonitorViewModel> monitors)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        if (monitors is null) throw new ArgumentNullException(nameof(monitors));

        Monitors = monitors as IList<MonitorViewModel> ?? monitors.ToList();

        InitializeComponent();
        DataContext = this;

        Loaded += OnLoaded;
        Closed += (_, _) => _tracker.TrackedWindowsChanged -= TrackerOnTrackedWindowsChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tracker.TrackedWindowsChanged += TrackerOnTrackedWindowsChanged;
        RefreshTrackedWindows();
        SelectedMonitor ??= Monitors.FirstOrDefault();
    }

    private void TrackerOnTrackedWindowsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshTrackedWindows);
            return;
        }

        RefreshTrackedWindows();
    }

    private void RefreshTrackedWindows()
    {
        var selectedId = SelectedTrackedWindow?.Id;
        var snapshot = _tracker.GetTrackedWindows();

        TrackedWindows.Clear();
        foreach (var info in snapshot)
        {
            if (!info.IsAlive)
                continue;

            TrackedWindows.Add(new TrackedWindowEntry(info));
        }

        if (TrackedWindows.Count == 0)
        {
            SelectedTrackedWindow = null;
            return;
        }

        SelectedTrackedWindow = selectedId.HasValue
            ? TrackedWindows.FirstOrDefault(t => t.Id == selectedId.Value) ?? TrackedWindows.First()
            : TrackedWindows.First();
    }

    private void SendToMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrackedWindow is null)
        {
            MessageBox.Show(this, "Select a window to send first.", "No Window Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SelectedMonitor is null)
        {
            MessageBox.Show(this, "Select a destination monitor first.", "No Monitor Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_tracker.TryGetWindow(SelectedTrackedWindow.Id, out var window) || window is null)
        {
            MessageBox.Show(this, "The selected window is no longer available and will be removed from the list.", "Window Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshTrackedWindows();
            return;
        }

        var targetMonitor = SelectedMonitor;
        var options = SelectedTrackedWindow.Options;

        try
        {
            DisplayLib.ApplyWindow(window, targetMonitor.Info, options.Maximize, options.WidthDip, options.HeightDip, options.MarginDip, tracker: _tracker);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Move Window Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
