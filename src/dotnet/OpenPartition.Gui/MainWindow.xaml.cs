using System.Windows;
using System.Windows.Controls;

namespace OpenPartition.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
        Loaded += async (_, _) => await _model.RefreshAsync();
    }

    private void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SegmentViewModel segment })
        {
            _model.Selected = segment;
        }
    }
}
