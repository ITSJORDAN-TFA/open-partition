using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenPartition.Model;
using OpenPartition.Operations;

namespace OpenPartition.Gui;

/// <summary>Small modal dialogs, built in code so each one stays in one place.</summary>
internal static class Dialogs
{
    private const long MiB = Units.MiB;

    /// <summary>Ask where to move <paramref name="p"/>. Returns the planned step, or null if cancelled.</summary>
    public static MoveStep? AskMove(Window owner, DiskInfo disk, PartitionInfo p)
    {
        var range = Planner.Range(disk, p)!;
        var name = Planner.Name(p);
        var moving = Planner.MovingWith(disk, p).ToList();
        var bar = PlacementBar.ForMove(name, Palette.Boot, p.Offset, p.Size, range.Min, range.Max);
        var strip = new DiskStrip(disk, p, moving);
        var before = NumberBox();
        var after = NumberBox();
        var summary = Text("", top: 6);

        void Refresh()
        {
            before.Text = ((bar.Start - range.Min) / MiB).ToString("N0", CultureInfo.CurrentCulture);
            after.Text = ((range.Max - bar.Start) / MiB).ToString("N0", CultureInfo.CurrentCulture);
            strip.Show(bar.Start, p.Size);
            summary.Text = bar.Start == p.Offset
                ? "Drag the partition, or type the free space you want before or after it."
                : $"New start: {Units.FormatBytes(bar.Start)}, {(bar.Start > p.Offset ? "right" : "left")} by {Units.FormatBytes(Math.Abs(bar.Start - p.Offset))}.";
        }
        bar.Changed += Refresh;
        OnEntered(before, mib => bar.Set(range.Min + (mib * MiB), p.Size), Refresh);
        OnEntered(after, mib => bar.Set(range.Max - (mib * MiB), p.Size), Refresh);

        var start = new Button { Content = "Start of the free space", Margin = new Thickness(0, 0, 6, 0) };
        start.Click += (_, _) => bar.Set(range.Min, p.Size);
        var end = new Button { Content = "End of the free space" };
        end.Click += (_, _) => bar.Set(range.Max, p.Size);

        var body = new StackPanel();
        body.Children.Add(Text($"Move {name} ({Units.FormatBytes(p.Size)}) within the free space around it."));
        if (p.Role == PartitionRole.Extended)
        {
            body.Children.Add(Text($"Its logical drives move with it: {string.Join(", ", moving.Skip(1).Select(Planner.Name))}.", muted: true));
        }
        body.Children.Add(Spaced(bar, 8));
        body.Children.Add(Fields(("Free space before (MiB)", before), ("Free space after (MiB)", after)));
        body.Children.Add(Spaced(Row(start, end), 8));
        body.Children.Add(summary);
        body.Children.Add(Text("Whole disk", muted: true, top: 10));
        body.Children.Add(strip);
        body.Children.Add(Text("Every volume that moves is locked for the whole move: close programs and stop services that use it first. Moving copies its data, which can take a long time on large partitions.", muted: true, top: 12));
        Refresh();
        return Show(owner, $"Move {name}", body, "Add to plan", () => Planner.Move(disk, p, bar.Start), width: 680);
    }

    /// <summary>Ask how far to extend <paramref name="p"/>. Returns the planned step, or null if cancelled.</summary>
    public static ExtendStep? AskExtend(Window owner, DiskInfo disk, PartitionInfo p)
    {
        var max = Planner.MaxSize(disk, p);
        var name = Planner.Name(p);
        var min = Math.Min(max, ((p.Size + MiB - 1) / MiB + 1) * MiB);
        var bar = PlacementBar.ForExtend(name, Palette.Boot, p.Offset, p.Size, max, min, max);
        var strip = new DiskStrip(disk, p, [p]);
        var size = NumberBox();
        var after = NumberBox();
        var summary = Text("", top: 6);

        long NewSize() => bar.Length;
        void Refresh()
        {
            size.Text = (bar.Length / MiB).ToString("N0", CultureInfo.CurrentCulture);
            after.Text = ((max - bar.Length) / MiB).ToString("N0", CultureInfo.CurrentCulture);
            strip.Show(p.Offset, bar.Length);
            summary.Text = $"New size: {Units.FormatBytes(bar.Length)} (+{Units.FormatBytes(bar.Length - p.Size)}).";
        }
        bar.Changed += Refresh;
        OnEntered(size, mib => bar.Set(p.Offset, mib * MiB), Refresh);
        OnEntered(after, mib => bar.Set(p.Offset, max - (mib * MiB)), Refresh);
        var all = new Button { Content = "Use all the free space" };
        all.Click += (_, _) => bar.Set(p.Offset, max);

        var body = new StackPanel();
        body.Children.Add(Text($"Extend {name} ({Units.FormatBytes(p.Size)}) into the free space directly after it."));
        body.Children.Add(Text("Windows does the resize itself (as Disk Management's Extend Volume does). It is safe while the volume is in use.", muted: true));
        body.Children.Add(Spaced(bar, 8));
        body.Children.Add(Fields(("New size (MiB)", size), ("Free space after (MiB)", after)));
        body.Children.Add(Spaced(Row(all), 8));
        body.Children.Add(summary);
        body.Children.Add(Text("Whole disk", muted: true, top: 10));
        body.Children.Add(strip);
        Refresh();
        return Show(owner, $"Extend {name}", body, "Add to plan", () => Planner.Extend(disk, p, NewSize()), width: 680);
    }

    private static TextBox NumberBox() => new()
    {
        Width = 130,
        Padding = new Thickness(4, 3, 4, 3),
        HorizontalContentAlignment = HorizontalAlignment.Right,
    };

    /// <summary>Apply a typed number of MiB on Enter or when the box loses focus; restore it if it doesn't parse.</summary>
    private static void OnEntered(TextBox box, Action<long> apply, Action restore)
    {
        void Commit()
        {
            if (long.TryParse(box.Text.Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim(),
                    NumberStyles.Integer, CultureInfo.CurrentCulture, out var mib) && mib >= 0)
            {
                apply(mib);
            }
            restore();
        }
        box.LostFocus += (_, _) => Commit();
        // Preview: Enter must set the value, not press the default button.
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
    }

    private static Grid Fields(params (string Label, TextBox Box)[] fields)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < fields.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = fields[i].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 14, 3) };
            Grid.SetRow(label, i);
            Grid.SetRow(fields[i].Box, i);
            Grid.SetColumn(fields[i].Box, 1);
            fields[i].Box.Margin = new Thickness(0, 3, 0, 3);
            grid.Children.Add(label);
            grid.Children.Add(fields[i].Box);
        }
        return grid;
    }

    private static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    /// <summary>
    /// Confirm and run the plan. Real Azure disks need a snapshot named first. Returns when the
    /// dialog closes; the caller refreshes afterwards.
    /// </summary>
    public static async Task ApplyAsync(Window owner, SystemSnapshot snapshot, IReadOnlyList<PlannedStep> steps)
    {
        var disks = steps.Select(s => s.DiskNumber).Distinct().Select(n => snapshot.Disks.First(d => d.Number == n)).ToList();
        var verdicts = disks.Select(d => (Disk: d, Verdict: WriteGate.For(snapshot, d))).ToList();
        var refused = verdicts.Where(v => v.Verdict == WriteVerdict.Refused).ToList();
        var needsSnapshot = verdicts.Any(v => v.Verdict == WriteVerdict.NeedsSnapshot);

        var body = new StackPanel();
        body.Children.Add(Text("These changes will be made, in this order:"));
        var list = new StackPanel { Margin = new Thickness(8, 4, 0, 8) };
        var stepTexts = steps.Select((s, i) => new TextBlock { Text = $"{i + 1}. {s.Describe()}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) }).ToList();
        stepTexts.ForEach(t => list.Children.Add(t));
        body.Children.Add(list);
        foreach (var (disk, verdict) in verdicts)
        {
            body.Children.Add(Text(WriteGate.Explain(verdict, disk), muted: verdict == WriteVerdict.Allowed, colour: verdict == WriteVerdict.Refused ? Palette.Blocker : null));
        }

        var snapshotName = new TextBox { Margin = new Thickness(0, 4, 0, 4) };
        var confirm = new CheckBox { Content = "I have taken that snapshot and know how to restore it.", Margin = new Thickness(0, 2, 0, 8) };
        if (needsSnapshot)
        {
            body.Children.Add(Text("Name of the snapshot you took (it is recorded in the log):", top: 8));
            body.Children.Add(snapshotName);
            body.Children.Add(confirm);
        }
        body.Children.Add(Text("Each move is journaled: if the machine restarts or the program is closed part way, nothing is lost and the move can be resumed. Steps already finished stay finished if a later one fails.", muted: true));

        var bar = new ProgressBar { Height = 14, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 4), Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        body.Children.Add(bar);
        body.Children.Add(status);

        var window = Frame(owner, "Apply planned changes", body, out var ok, out var cancel);
        ok.Content = "Apply";
        var measuring = true;
        void Refresh() => ok.IsEnabled = !measuring && refused.Count == 0 && (!needsSnapshot || (confirm.IsChecked == true && snapshotName.Text.Trim().Length > 0));
        confirm.Checked += (_, _) => Refresh();
        confirm.Unchecked += (_, _) => Refresh();
        snapshotName.TextChanged += (_, _) => Refresh();
        Refresh();

        body.Children.Add(Text("Estimates include fingerprinting and verified copying. Azure disk tiers and VM sizes both cap throughput; caching, other workloads and NTFS free-space skipping can change the actual time.", muted: true));
        window.Loaded += async (_, _) =>
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var estimate = "Windows resize: duration depends on the file system";
                if (steps[i] is MoveStep move)
                {
                    stepTexts[i].Text += " — measuring read speed…";
                    try
                    {
                        var rate = await Engine.BenchAsync(move.DiskNumber, move.From, move.Length);
                        estimate = rate is { } speed
                            ? Throughput.FormatDuration(Throughput.EstimateMove(Throughput.BytesToCopy(snapshot, move), speed))
                            : "estimate unavailable (read benchmark failed)";
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.IO.IOException or OperationFailedException)
                    {
                        estimate = "estimate unavailable (read benchmark could not run)";
                    }
                }
                stepTexts[i].Text = $"{i + 1}. {steps[i].Describe()} — {estimate}";
            }
            measuring = false;
            Refresh();
        };

        var running = false;
        var stop = false;
        window.Closing += (_, e) => e.Cancel = running;
        var stopButton = new Button { Content = "Stop after this step", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        stopButton.Click += (_, _) =>
        {
            stop = true;
            stopButton.IsEnabled = false;
            stopButton.Content = "Stopping after this stepâ€¦";
        };
        ((Panel)ok.Parent).Children.Insert(0, stopButton);
        ok.Click += async (_, _) =>
        {
            running = true;
            ok.IsEnabled = false;
            cancel.IsEnabled = false;
            stopButton.Visibility = steps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            snapshotName.IsEnabled = false;
            confirm.IsEnabled = false;
            bar.Visibility = Visibility.Visible;
            var progress = new Progress<(int Index, StepProgress Progress)>(p =>
            {
                for (var i = 0; i < stepTexts.Count; i++)
                {
                    stepTexts[i].FontWeight = i == p.Index ? FontWeights.SemiBold : FontWeights.Normal;
                }
                bar.IsIndeterminate = p.Progress.Fraction is null;
                if (p.Progress.Fraction is { } f)
                {
                    bar.Value = f;
                }
                status.Text = $"Step {p.Index + 1} of {steps.Count}: {p.Progress.Text}";
            });
            var result = await Executor.RunAsync(snapshot, steps, needsSnapshot ? snapshotName.Text.Trim() : null, progress, stopRequested: () => stop);
            running = false;
            stopButton.Visibility = Visibility.Collapsed;
            bar.IsIndeterminate = false;
            if (result.Stopped)
            {
                status.Text = $"Stopped as asked: steps 1 to {result.Completed} are done, the rest were not started.";
                status.Foreground = Palette.Info;
            }
            else if (result.Succeeded)
            {
                bar.Value = 1;
                status.Text = $"All {steps.Count} change(s) done.";
                status.Foreground = Palette.Planned;
            }
            else
            {
                status.Text = $"Step {result.Completed + 1} failed: {result.Error}" + (result.Completed > 0 ? $" Steps 1 to {result.Completed} are done; the rest were not started." : "");
                status.Foreground = Palette.Blocker;
            }
            cancel.Content = "Close";
            cancel.IsEnabled = true;
        };
        window.ShowDialog();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Finish an interrupted move, or (<paramref name="undo"/>) put the partition back where it was.</summary>
    public static void Resume(Window owner, SystemSnapshot snapshot, PendingRow pending, bool undo)
    {
        var disk = pending.Disk!;
        var verdict = WriteGate.For(snapshot, disk);
        var body = new StackPanel();
        body.Children.Add(Text(pending.Text));
        if (undo)
        {
            body.Children.Add(Text("Undo puts the partition back at its original location, exactly as it was before the move, with the same drive letters. "
                + "The move never wrote to that location, so nothing there has changed. The partial copy is discarded.", top: 8));
        }
        body.Children.Add(Text(WriteGate.Explain(verdict, disk), muted: verdict == WriteVerdict.Allowed, colour: verdict == WriteVerdict.Refused ? Palette.Blocker : null));
        var snapshotName = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
        if (verdict == WriteVerdict.NeedsSnapshot)
        {
            body.Children.Add(Text("Name of the snapshot you took before the move:", top: 8));
            body.Children.Add(snapshotName);
        }
        var bar = new ProgressBar { Height = 14, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 4), Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(bar);
        body.Children.Add(status);
        var window = Frame(owner, undo ? "Undo interrupted move" : "Resume interrupted move", body, out var ok, out var cancel);
        ok.Content = undo ? "Undo" : "Resume";
        ok.IsEnabled = verdict == WriteVerdict.Allowed || verdict == WriteVerdict.NeedsSnapshot;
        var running = false;
        window.Closing += (_, e) => e.Cancel = running;
        ok.Click += async (_, _) =>
        {
            if (verdict == WriteVerdict.NeedsSnapshot && snapshotName.Text.Trim().Length == 0)
            {
                status.Text = "Name the snapshot first.";
                return;
            }
            running = true;
            ok.IsEnabled = cancel.IsEnabled = false;
            bar.Visibility = Visibility.Visible;
            try
            {
                var name = verdict == WriteVerdict.NeedsSnapshot ? snapshotName.Text.Trim() : null;
                var progress = new Progress<StepProgress>(p =>
                {
                    bar.IsIndeterminate = p.Fraction is null;
                    if (p.Fraction is { } f) bar.Value = f;
                    status.Text = p.Text;
                });
                if (undo)
                {
                    await Engine.UndoAsync(pending.Move, disk.Number, name, progress);
                }
                else
                {
                    await Engine.RecoverAsync(pending.Move, disk.Number, name, progress);
                }
                bar.IsIndeterminate = false;
                bar.Value = 1;
                status.Text = undo ? "Undone: the partition is back where it was. Refresh to see it." : "The move is finished.";
                status.Foreground = Palette.Planned;
            }
            catch (OperationFailedException e)
            {
                bar.IsIndeterminate = false;
                status.Text = (undo ? "Could not undo the move: " : "Could not finish the move: ") + VolumeUsers.Explain(e.Message, snapshot)
                    + (!undo && pending.Move.CanUndo ? " You can Undo the move instead: the partition goes back where it was." : "");
                status.Foreground = Palette.Blocker;
            }
            running = false;
            cancel.Content = "Close";
            cancel.IsEnabled = true;
        };
        window.ShowDialog();
    }

    private static T? Show<T>(Window owner, string title, UIElement body, string okText, Func<T> create, double width = 560) where T : class
    {
        T? result = null;
        var error = Text("", colour: Palette.Blocker);
        var panel = new StackPanel();
        panel.Children.Add(body);
        panel.Children.Add(error);
        var window = Frame(owner, title, panel, out var ok, out _);
        window.Width = width;
        ok.Content = okText;
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            try
            {
                result = create();
                window.DialogResult = true;
            }
            catch (PlanException e)
            {
                error.Text = e.Message;
            }
        };
        return window.ShowDialog() == true ? result : null;
    }

    private static Window Frame(Window owner, string title, UIElement body, out Button ok, out Button cancel)
    {
        ok = new Button { Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), MinWidth = 96, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 6, 18, 6), IsCancel = true, MinWidth = 96 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Content = root,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            Foreground = owner.Foreground,
            Background = Brushes.White,
            Icon = owner.Icon,
            ShowInTaskbar = false,
        };
        var closeButton = cancel;
        closeButton.Click += (_, _) =>
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        };
        return window;
    }

    private static TextBlock Text(string text, bool muted = false, Brush? colour = null, double top = 0) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 4),
        Foreground = colour ?? (muted ? Palette.Info : Brushes.Black),
    };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children)
        {
            row.Children.Add(c);
        }
        return row;
    }

    internal static string Bytes(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
}
