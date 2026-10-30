using System.Windows;
using System.Windows.Controls;

namespace OpenPartition.Gui;

/// <summary>
/// Lays children out left to right with widths proportional to their <see cref="WeightProperty"/>,
/// but never narrower than <see cref="MinChildWidth"/>, so a 16 MB partition on a 2 TB disk
/// is still visible and clickable.
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty MinChildWidthProperty = DependencyProperty.Register(
        nameof(MinChildWidth), typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(56.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static double GetWeight(DependencyObject element) => (double)element.GetValue(WeightProperty);

    public static void SetWeight(DependencyObject element, double value) => element.SetValue(WeightProperty, value);

    public double MinChildWidth
    {
        get => (double)GetValue(MinChildWidthProperty);
        set => SetValue(MinChildWidthProperty, value);
    }

    private double[] Widths(double available)
    {
        var children = InternalChildren.Cast<UIElement>().ToArray();
        var widths = new double[children.Length];
        if (children.Length == 0 || double.IsInfinity(available))
        {
            Array.Fill(widths, MinChildWidth * 2);
            return widths;
        }
        var min = Math.Min(MinChildWidth, available / children.Length);
        var spare = Math.Max(0, available - (min * children.Length));
        var totalWeight = children.Sum(c => Math.Max(0, GetWeight(c)));
        for (var i = 0; i < children.Length; i++)
        {
            var share = totalWeight > 0 ? Math.Max(0, GetWeight(children[i])) / totalWeight : 1.0 / children.Length;
            widths[i] = min + (spare * share);
        }
        return widths;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var widths = Widths(availableSize.Width);
        var height = 0.0;
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(widths[i++], availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? widths.Sum() : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Widths(finalSize.Width);
        var x = 0.0;
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i++];
        }
        return finalSize;
    }
}
