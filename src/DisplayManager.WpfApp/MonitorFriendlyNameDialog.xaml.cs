using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace DisplayManager.WpfApp;

public partial class MonitorFriendlyNameDialog : Window, INotifyPropertyChanged
{
    public MonitorFriendlyNameDialog(MonitorViewModel monitor)
    {
        InitializeComponent();
        Monitor = monitor;
        FriendlyName = monitor.AssignedName ?? monitor.Info.FriendlyName;
        DataContext = this;
    }

    public MonitorViewModel Monitor { get; }

    private string _friendlyName = string.Empty;
    public string FriendlyName
    {
        get => _friendlyName;
        set
        {
            if (!string.Equals(_friendlyName, value, StringComparison.Ordinal))
            {
                _friendlyName = value;
                OnPropertyChanged();
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
