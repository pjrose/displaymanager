using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace DisplayManager.WpfApp;

public partial class FriendlyNamePromptDialog : Window
{
    public FriendlyNamePromptDialog(IEnumerable<string> friendlyNames)
    {
        InitializeComponent();
        FriendlyNames = new ObservableCollection<string>(friendlyNames);
        if (FriendlyNames.Count > 0)
        {
            SelectedName = FriendlyNames[0];
        }
        DataContext = this;
    }

    public ObservableCollection<string> FriendlyNames { get; }

    public string SelectedName { get; set; } = string.Empty;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedName))
        {
            MessageBox.Show(this, "Enter or choose a friendly name.", "Friendly Name", MessageBoxButton.OK, MessageBoxImage.Information);
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
