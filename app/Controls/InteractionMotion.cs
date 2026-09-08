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
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Scale(button, button.Name == "PlayButton" ? .94f : button.Content is FontIcon ? .86f : .95f)), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Scale(button, 1)), true);
        button.PointerCanceled += (_, _) => Scale(button, 1);
        button.PointerCaptureLost += (_, _) => Scale(button, 1);
        button.PointerExited += (_, _) => Scale(button, 1);
        button.Unloaded += (_, _) => Scale(button, 1);
    }
    public static void AttachItem(Control item,bool album)
    {
        if((bool)item.GetValue(AttachedProperty))return;item.SetValue(AttachedProperty,true);bool hover=false;
        void Move(float scale,float y,bool instant=false){var v=ElementCompositionPreview.GetElementVisual(item);ElementCompositionPreview.SetIsTranslationEnabled(item,true);v.CenterPoint=new((float)item.ActualWidth/2,(float)item.ActualHeight/2,0);v.StopAnimation("Scale");v.StopAnimation("Translation");if(Reduced){v.Scale=Vector3.One;v.Properties.InsertVector3("Translation",Vector3.Zero);return;}if(instant){v.Scale=new(scale,scale,1);return;}var spring=v.Compositor.CreateSpringVector3Animation();spring.FinalValue=new(scale,scale,1);spring.DampingRatio=.8f;spring.Period=TimeSpan.FromSeconds(.18);v.StartAnimation("Scale",spring);var lift=v.Compositor.CreateSpringVector3Animation();lift.FinalValue=new(0,y,0);lift.DampingRatio=1;lift.Period=TimeSpan.FromSeconds(.3);v.StartAnimation("Translation",lift);}
        item.PointerEntered+=(_,_)=>{hover=true;if(album)Move(1.035f,-4);};
        item.PointerExited+=(_,_)=>{hover=false;Move(1,0);};
        item.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>Move(.97f,0,true)),true);
        item.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,_)=>Move(album&&hover?1.035f:1,album&&hover?-4:0)),true);
        item.PointerCanceled+=(_,_)=>Move(1,0);item.PointerCaptureLost+=(_,_)=>Move(1,0);item.Unloaded+=(_,_)=>Move(1,0);
    }
    static void Scale(Button button, float value)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        visual.CenterPoint = new((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
        visual.StopAnimation("Scale");
        if (Reduced) { visual.Scale = Vector3.One; return; }
        if (value < 1) { visual.Scale = new(value, value, 1); return; }
        var animation = visual.Compositor.CreateSpringVector3Animation();
        animation.FinalValue = Vector3.One; animation.DampingRatio = .8f; animation.Period = TimeSpan.FromSeconds(.18);
        visual.StartAnimation("Scale", animation);
    }
}
