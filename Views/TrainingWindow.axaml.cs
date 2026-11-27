using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Views;

/// <summary>
/// Window of the guided training. The code-behind only does geometry: it finds the control the current step names,
/// opens the hole of the dim layer over it and places the text panel beside it.
/// </summary>
public partial class TrainingWindow : Window
{
    private const double HolePadding = 4;
    private const double Gap = 12;
    private const double Edge = 16;

    private TrainingSessionViewModel? _session;
    private Control? _target;
    private string? _targetAnchor;

    public TrainingWindow()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => UpdateOverlay();
    }

    /// <summary>The area being highlighted, in window coordinates; null when the step has no visible target.</summary>
    internal Rect? HighlightedArea => Scrim.Hole;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_session is not null)
        {
            _session.Runner.PropertyChanged -= OnRunnerChanged;
            _session.CloseRequested -= OnCloseRequested;
        }
        _session = DataContext as TrainingSessionViewModel;
        if (_session is not null)
        {
            _session.Runner.PropertyChanged += OnRunnerChanged;
            _session.CloseRequested += OnCloseRequested;
        }
        UpdateOverlay();
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>A new step may name a control that is only laid out after the screen changes.</summary>
    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateOverlay, DispatcherPriority.Background);

    internal void UpdateOverlay()
    {
        if (_session is null || Scrim.Bounds.Width <= 0)
            return;

        var area = new Rect(Scrim.Bounds.Size);
        Rect? hole = null;
        if (FindTarget(_session.Runner.Anchor) is { } target && target.TranslatePoint(default, Scrim) is { } origin)
        {
            var rect = new Rect(origin, target.Bounds.Size).Inflate(HolePadding).Intersect(area);
            if (rect.Width > 0 && rect.Height > 0)
                hole = rect;
        }
        if (Scrim.Hole != hole)
            Scrim.Hole = hole;

        Callout.Measure(Size.Infinity);
        var position = Place(Callout.DesiredSize, hole, area);
        if (Canvas.GetLeft(Callout) != position.X)
            Canvas.SetLeft(Callout, position.X);
        if (Canvas.GetTop(Callout) != position.Y)
            Canvas.SetTop(Callout, position.Y);
    }

    /// <summary>The last visible control with the anchor: in a transcript, the newest one.</summary>
    private Control? FindTarget(string? anchor)
    {
        if (anchor is null)
            return _target = null;
        if (_target is not null && _targetAnchor == anchor && _target.IsEffectivelyVisible
            && _target.IsAttachedToVisualTree() && Tutorial.GetAnchor(_target) == anchor)
            return _target;

        _targetAnchor = anchor;
        _target = this.GetVisualDescendants().OfType<Control>()
            .LastOrDefault(c => Tutorial.GetAnchor(c) == anchor && c.IsEffectivelyVisible);
        // A menu button may be scrolled out of the menu column.
        _target?.BringIntoView();
        return _target;
    }

    /// <summary>
    /// Beside the hole when there is room (right, left, below, above); otherwise in the top right corner of the hole,
    /// which is the part of a screen with the least to click. Without a hole, the middle of the window.
    /// </summary>
    internal static Point Place(Size callout, Rect? hole, Rect area)
    {
        double ClampX(double x) => Math.Clamp(x, Edge, Math.Max(Edge, area.Width - callout.Width - Edge));
        double ClampY(double y) => Math.Clamp(y, Edge, Math.Max(Edge, area.Height - callout.Height - Edge));

        if (hole is not { } h)
            return new Point(ClampX((area.Width - callout.Width) / 2), ClampY((area.Height - callout.Height) / 2));

        if (h.Right + Gap + callout.Width + Edge <= area.Width)
            return new Point(h.Right + Gap, ClampY(h.Top));
        if (h.Left - Gap - callout.Width >= Edge)
            return new Point(h.Left - Gap - callout.Width, ClampY(h.Top));
        if (h.Bottom + Gap + callout.Height + Edge <= area.Height)
            return new Point(ClampX(h.Left), h.Bottom + Gap);
        if (h.Top - Gap - callout.Height >= Edge)
            return new Point(ClampX(h.Left), h.Top - Gap - callout.Height);
        return new Point(ClampX(h.Right - callout.Width - Edge), ClampY(h.Top + Edge));
    }
}
