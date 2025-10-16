using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;

namespace DisplayManager.WpfApp;

public partial class MonitorPickerDialog : Window
{
    public MonitorPickerDialog(IEnumerable<MonitorViewModel> monitors)
    {
        InitializeComponent();
        Monitors = new ObservableCollection<MonitorViewModel>(monitors);
        if (Monitors.Count > 0)
        {
            SelectedMonitor = Monitors[0];
        }
        DataContext = this;
    }

    public ObservableCollection<MonitorViewModel> Monitors { get; }

    public MonitorViewModel? SelectedMonitor { get; set; }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMonitor is null)
        {
            MessageBox.Show(this, "Choose a monitor before continuing.", "Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
