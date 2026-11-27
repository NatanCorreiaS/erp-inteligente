using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;

namespace erp_inteligente.Views;

/// <summary>
/// The dim layer of the training overlay (references/UI.md, section 10): covers the window except for
/// <see cref="Hole"/>, which is outlined. Pointer input passes only through the hole, so the person can use the
/// highlighted control and nothing else. Square corners, no blur and no animation.
/// </summary>
public sealed class TutorialScrim : Control, ICustomHitTest
{
    public const double OutlineThickness = 2;

    public static readonly StyledProperty<Rect?> HoleProperty =
        AvaloniaProperty.Register<TutorialScrim, Rect?>(nameof(Hole));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TutorialScrim, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> OutlineProperty =
        AvaloniaProperty.Register<TutorialScrim, IBrush?>(nameof(Outline));

    static TutorialScrim()
    {
        AffectsRender<TutorialScrim>(HoleProperty, FillProperty, OutlineProperty);
    }

    /// <summary>The highlighted area, in this control's coordinates; null dims everything.</summary>
    public Rect? Hole
    {
        get => GetValue(HoleProperty);
        set => SetValue(HoleProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Outline
    {
        get => GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    /// <summary>The dimmed area takes the input; the hole lets it through to the control below.</summary>
    public bool HitTest(Point point) => Hole is not { } hole || !hole.Contains(point);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Fill is not { } fill)
            return;
        if (Hole is not { } hole)
        {
            context.FillRectangle(fill, bounds);
            return;
        }

        // Four bands around the hole.
        context.FillRectangle(fill, new Rect(0, 0, bounds.Width, hole.Top));
        context.FillRectangle(fill, new Rect(0, hole.Bottom, bounds.Width, System.Math.Max(0, bounds.Height - hole.Bottom)));
        context.FillRectangle(fill, new Rect(0, hole.Top, hole.Left, hole.Height));
        context.FillRectangle(fill, new Rect(hole.Right, hole.Top, System.Math.Max(0, bounds.Width - hole.Right), hole.Height));
        if (Outline is { } outline)
            context.DrawRectangle(new Pen(outline, OutlineThickness), hole.Deflate(OutlineThickness / 2));
    }
}
