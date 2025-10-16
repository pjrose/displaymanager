using System;
using System.Collections.ObjectModel;
using System.Windows;
using DisplayManagerLib;
using DisplayLib = DisplayManagerLib.DisplayManager;

namespace DisplayManager.WpfApp;

public partial class OrientationDialog : Window
{
    public OrientationDialog()
    {
        InitializeComponent();
        Orientations = new ObservableCollection<DisplayLib.Orientations>(Enum.GetValues<DisplayLib.Orientations>());
        SelectedOrientation = DisplayLib.Orientations.DEGREES_CW_0;
        DataContext = this;
    }

    public ObservableCollection<DisplayLib.Orientations> Orientations { get; }

    public DisplayLib.Orientations SelectedOrientation { get; set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
