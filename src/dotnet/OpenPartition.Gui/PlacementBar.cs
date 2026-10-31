using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenPartition.Model;

namespace OpenPartition.Gui;

/// <summary>
/// The free space a partition can use, drawn as a track with the partition as a block on it.
/// In move mode the block is dragged along the track (a dashed outline marks where it is now);
/// in extend mode its right edge is dragged. Positions snap to 1 MiB. The owner reads
/// <see cref="Start"/> and <see cref="Length"/> and listens to <see cref="Changed"/>.
/// </summary>
internal sealed class PlacementBar : Border
{
    private const long MiB = Units.MiB;
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Border _block;
    private readonly Border _handle;
    private readonly Rectangle _ghost;
    private readonly TextBlock _title;
    private readonly TextBlock _size;
    private readonly long _trackStart;
    private readonly long _trackEnd;
    private readonly long _originalStart;
    private readonly long _originalLength;
    private readonly bool _resize;
    private Point _dragFrom;
    private long _dragStartValue;
    private bool _draggingBlock;
    private bool _draggingEdge;

    /// <summary>Move mode: the block of <paramref name="length"/> bytes can start anywhere in [<paramref name="minStart"/>, <paramref name="maxStart"/>].</summary>
    public static PlacementBar ForMove(string name, Brush fill, long start, long length, long minStart, long maxStart) =>
        new(name, fill, start, length, length, minStart, maxStart + length, resize: false) { MinStart = minStart, MaxStart = maxStart, MinLength = length, MaxLength = length };

    /// <summary>
    /// Extend mode: the block starts at <paramref name="start"/>, now <paramref name="currentLength"/> long
    /// (shown as a dashed outline), and can grow from <paramref name="minLength"/> to <paramref name="maxLength"/>.
    /// </summary>
    public static PlacementBar ForExtend(string name, Brush fill, long start, long currentLength, long length, long minLength, long maxLength) =>
        new(name, fill, start, length, currentLength, start, start + maxLength, resize: true) { MinStart = start, MaxStart = start, MinLength = minLength, MaxLength = maxLength };

    private PlacementBar(string name, Brush fill, long start, long length, long originalLength, long trackStart, long trackEnd, bool resize)
    {
        _originalLength = originalLength;
        _trackStart = trackStart;
        _trackEnd = Math.Max(trackEnd, trackStart + 1);
        _originalStart = start;
        _resize = resize;
        Start = start;
        Length = length;

        Height = 78;
        CornerRadius = new CornerRadius(6);
        Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF5));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE3));
        BorderThickness = new Thickness(1);
        Child = _canvas;

        _ghost = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xB5)),
            StrokeDashArray = [4, 3],
            RadiusX = 5,
            RadiusY = 5,
            IsHitTestVisible = false,
        };
        _title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _size = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0x6B, 0x7A)), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        var content = new DockPanel();
        var bar = new Border { Height = 7, Background = fill, CornerRadius = new CornerRadius(4, 4, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        content.Children.Add(bar);
        content.Children.Add(new StackPanel { Margin = new Thickness(8, 5, 8, 4), Children = { _title, _size } });
        _block = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Child = content,
            Cursor = resize ? Cursors.Arrow : Cursors.SizeAll,
            ToolTip = resize ? "Drag the right edge to choose the new size" : "Drag to choose where the partition goes",
        };
        _handle = new Border
        {
            Width = 10,
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
            CornerRadius = new CornerRadius(0, 5, 5, 0),
            Cursor = Cursors.SizeWE,
            Visibility = resize ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock { Text = "⋮", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _canvas.Children.Add(_block);
        // In extend mode the outline of the current size sits on top of the growing block.
        _canvas.Children.Insert(resize ? 1 : 0, _ghost);
        _canvas.Children.Add(_handle);

        _block.MouseLeftButtonDown += (_, e) => BeginDrag(e, edge: false);
        _handle.MouseLeftButtonDown += (_, e) => BeginDrag(e, edge: true);
        _canvas.MouseMove += OnDrag;
        _canvas.MouseLeftButtonUp += (_, _) => EndDrag();
        _canvas.LostMouseCapture += (_, _) => EndDrag();
        _canvas.SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Layout();
    }

    public long Start { get; private set; }
    public long Length { get; private set; }
    public long MinStart { get; private init; }
    public long MaxStart { get; private init; }
    public long MinLength { get; private init; }
    public long MaxLength { get; private init; }

    /// <summary>Raised whenever <see cref="Start"/> or <see cref="Length"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Place the block (clamped to what is allowed); from the text boxes and buttons.</summary>
    public void Set(long start, long length)
    {
        start = Math.Clamp(start, MinStart, MaxStart);
        length = Math.Clamp(length, MinLength, MaxLength);
        if (start == Start && length == Length)
        {
            return;
        }
        Start = start;
        Length = length;
        Layout();
        Changed?.Invoke();
    }

    private double Scale => _canvas.ActualWidth / (_trackEnd - _trackStart);

    private void Layout()
    {
        if (_canvas.ActualWidth <= 0)
        {
            return;
        }
        var height = _canvas.ActualHeight - 12;
        double X(long offset) => (offset - _trackStart) * Scale;
        var width = Math.Max(54, Length * Scale);
        var left = Math.Min(X(Start), _canvas.ActualWidth - width);
        _block.Width = width;
        _block.Height = height;
        Canvas.SetLeft(_block, left);
        Canvas.SetTop(_block, 6);
        _ghost.Width = Math.Max(54, _originalLength * Scale);
        _ghost.Height = height;
        Canvas.SetLeft(_ghost, Math.Min(X(_originalStart), _canvas.ActualWidth - _ghost.Width));
        Canvas.SetTop(_ghost, 6);
        _handle.Height = height;
        Canvas.SetLeft(_handle, left + width - _handle.Width);
        Canvas.SetTop(_handle, 6);
        _size.Text = Units.FormatBytes(Length);
    }

    private void BeginDrag(MouseButtonEventArgs e, bool edge)
    {
        if (edge != _resize)
        {
            return;
        }
        _dragFrom = e.GetPosition(_canvas);
        _dragStartValue = edge ? Length : Start;
        _draggingEdge = edge;
        _draggingBlock = !edge;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (!(_draggingBlock || _draggingEdge) || Scale <= 0)
        {
            return;
        }
        var bytes = (long)((e.GetPosition(_canvas).X - _dragFrom.X) / Scale);
        var value = (_dragStartValue + bytes) / MiB * MiB;
        if (_draggingBlock)
        {
            // Snap to the ends, where most moves go.
            var snap = (long)(8 / Scale);
            value = value - MinStart < snap ? MinStart : MaxStart - value < snap ? MaxStart : value;
            Set(value, Length);
        }
        else
        {
            var snap = (long)(8 / Scale);
            value = MaxLength - value < snap ? MaxLength : value;
            Set(Start, value);
        }
    }

    private void EndDrag()
    {
        _draggingBlock = _draggingEdge = false;
        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }
}

/// <summary>
/// The whole disk as a thin strip, each partition in its colour at its position, with one
/// partition shown where it will be. Updated as the user drags.
/// </summary>
internal sealed class DiskStrip : Border
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly DiskInfo _disk;
    private readonly PartitionInfo _subject;
    private readonly IReadOnlyCollection<PartitionInfo> _moving;
    private long _start;
    private long _length;

    public DiskStrip(DiskInfo disk, PartitionInfo subject, IReadOnlyCollection<PartitionInfo> movingWith)
    {
        _disk = disk;
        _subject = subject;
        _moving = movingWith;
        _start = subject.Offset;
        _length = subject.Size;
        Height = 26;
        CornerRadius = new CornerRadius(4);
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE3));
        BorderThickness = new Thickness(1);
        Child = _canvas;
        _canvas.SizeChanged += (_, _) => Draw();
    }

    public void Show(long start, long length)
    {
        _start = start;
        _length = length;
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        var width = _canvas.ActualWidth;
        if (width <= 0 || _disk.Size <= 0)
        {
            return;
        }
        var scale = width / _disk.Size;
        void Add(long offset, long size, Brush fill, bool highlight)
        {
            var r = new Rectangle
            {
                Width = Math.Max(3, size * scale - 1),
                Height = _canvas.ActualHeight - 6,
                Fill = fill,
                RadiusX = 2,
                RadiusY = 2,
                Stroke = highlight ? new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x30)) : null,
                StrokeThickness = highlight ? 1.5 : 0,
            };
            Canvas.SetLeft(r, offset * scale);
            Canvas.SetTop(r, 3);
            _canvas.Children.Add(r);
        }
        var shift = _start - _subject.Offset;
        foreach (var p in _disk.Partitions.Where(p => p.Role != PartitionRole.Extended && !_moving.Contains(p)))
        {
            Add(p.Offset, p.Size, p.IsBoot ? Palette.Boot : p.Role switch
            {
                PartitionRole.Data => Palette.Msr,
                _ => Palette.Free,
            }, false);
        }
        foreach (var p in _moving.Where(p => p.Role != PartitionRole.Extended))
        {
            Add(p.Offset + shift, p == _subject ? _length : p.Size, Palette.Efi, false);
        }
        Add(_start, _length, Palette.Tint(Palette.Boot), true);
    }
}
