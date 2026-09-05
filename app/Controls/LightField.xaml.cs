using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using System.Numerics;
using Windows.UI;
namespace LumaMusic.Controls;
public sealed partial class LightField : Microsoft.UI.Xaml.Controls.UserControl
{
    public volatile bool Reduced;
    uint target=0xff5a8172;Vector3 current=new(90,129,114);
    public LightField(){InitializeComponent();Unloaded+=(_,_)=>Canvas.Paused=true;Loaded+=(_,_)=>Canvas.Paused=false;}
    public void SetColor(Color c)=>Interlocked.Exchange(ref target,0xff000000u|((uint)c.R<<16)|((uint)c.G<<8)|c.B);
    public void Pause(bool pause)=>Canvas.Paused=pause;
    void Draw(ICanvasAnimatedControl sender,CanvasAnimatedDrawEventArgs args)
    {
        var d=args.DrawingSession;float w=(float)sender.Size.Width,h=(float)sender.Size.Height;if(w<=0||h<=0)return;
        double t=Reduced?0:args.Timing.TotalTime.TotalSeconds*.10;
        uint color=target;current=Vector3.Lerp(current,new((color>>16)&255,(color>>8)&255,color&255),.012f);
        d.Clear(Color.FromArgb(255,13,20,20));
        for(int i=0;i<4;i++){
            float x=w*(.34f+i*.19f+(float)Math.Sin(t+i*1.9)*.12f),y=h*(.35f+(float)Math.Cos(t*.8+i*2)*.3f);
            var c=Color.FromArgb((byte)(i==0?105:45),(byte)Math.Clamp(current.X+i*10,0,255),(byte)Math.Clamp(current.Y+i*5,0,255),(byte)Math.Clamp(current.Z+i*16,0,255));
            using var brush=new CanvasRadialGradientBrush(sender,c,Color.FromArgb(0,c.R,c.G,c.B)){Center=new(x,y),RadiusX=w*.48f,RadiusY=h*.65f};d.FillRectangle(0,0,w,h,brush);
        }
        // Very restrained contour light: a coherent optical field, not a distracting visualizer.
        using var line=new CanvasLinearGradientBrush(sender,new[]{new CanvasGradientStop{Position=0,Color=Color.FromArgb(0,240,255,240)},new CanvasGradientStop{Position=.5f,Color=Color.FromArgb(10,240,255,240)},new CanvasGradientStop{Position=1,Color=Color.FromArgb(0,240,255,240)}}){StartPoint=new(0,0),EndPoint=new(w,h)};
        for(int i=0;i<5;i++)d.DrawEllipse(new(w*.78f,h*.68f),w*(.30f+i*.035f),h*(.29f+i*.04f),line,1);
    }
}
