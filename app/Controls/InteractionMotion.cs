using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Numerics;

namespace LumaMusic.Controls;

public static class InteractionMotion
{
    public static bool Reduced { get; set; }
    static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false));
    public static void Attach(Button button)
    {
        if ((bool)button.GetValue(AttachedProperty)) return;
        button.SetValue(AttachedProperty, true);
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Scale(button, .98f)), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Scale(button, 1)), true);
        button.PointerCanceled += (_, _) => Scale(button, 1);
        button.PointerCaptureLost += (_, _) => Scale(button, 1);
        button.PointerExited += (_, _) => Scale(button, 1);
        button.Unloaded += (_, _) => Scale(button, 1);
    }
    static void Scale(Button button, float value)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        visual.CenterPoint = new((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
        visual.StopAnimation("Scale");
        if (Reduced) { visual.Scale = Vector3.One; return; }
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new(value, value, 1)); animation.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Scale", animation);
    }
}
