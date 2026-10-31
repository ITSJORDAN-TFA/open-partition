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

    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is { Disk: { } disk, Partition: { } partition, CanMove: true } && Dialogs.AskMove(this, disk, partition) is { } step)
        {
            _model.Enqueue(step);
        }
    }

    private void OnExtendClick(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is { Disk: { } disk, Partition: { } partition, CanExtend: true } && Dialogs.AskExtend(this, disk, partition) is { } step)
        {
            _model.Enqueue(step);
        }
    }

    private void OnTableRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TableRow { Segment: { } segment } })
        {
            _model.Selected = segment;
        }
    }

    private void OnShowOverviewClick(object sender, RoutedEventArgs e) => _model.ShowPlan = false;

    private void OnShowPlanClick(object sender, RoutedEventArgs e) => _model.ShowPlan = _model.HasQueue;

    private void OnUndoClick(object sender, RoutedEventArgs e) => _model.UndoLast();

    private void OnClearClick(object sender, RoutedEventArgs e) => _model.ClearQueue();

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_model.Snapshot is not { } snapshot || _model.Queue.Count == 0)
        {
            return;
        }
        await Dialogs.ApplyAsync(this, snapshot, _model.Queue.Select(q => q.Step).ToList());
        await _model.RefreshAsync();
    }

    private async void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PendingRow { Disk: not null } pending } && _model.Snapshot is { } snapshot)
        {
            Dialogs.Resume(this, snapshot, pending, undo: false);
            await _model.RefreshAsync();
        }
    }

    private async void OnUndoMoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PendingRow { Disk: not null } pending } && _model.Snapshot is { } snapshot)
        {
            Dialogs.Resume(this, snapshot, pending, undo: true);
            await _model.RefreshAsync();
        }
    }
}
