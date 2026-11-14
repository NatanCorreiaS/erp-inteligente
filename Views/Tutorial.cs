using Avalonia;
using Avalonia.Controls;

namespace erp_inteligente.Views;

/// <summary>
/// Names a control so a training step can highlight it (<c>views:Tutorial.Anchor="nexus:request"</c>). The names are
/// those of <see cref="Training.TutorialAnchors"/>. Outside the training window the name has no effect.
/// </summary>
public static class Tutorial
{
    public static readonly AttachedProperty<string?> AnchorProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Anchor", typeof(Tutorial));

    public static string? GetAnchor(Control control) => control.GetValue(AnchorProperty);

    public static void SetAnchor(Control control, string? value) => control.SetValue(AnchorProperty, value);
}
