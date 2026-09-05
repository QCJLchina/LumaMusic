using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
namespace LumaMusic.Controls;
// 沉浸式背景：专辑封面高斯模糊铺满整个窗口，切歌时交叉淡化，静止后自动休眠。
// 封面位图延迟到首次绘制时才加载——控件折叠期间创建资源会静默失败。
public sealed class AmbientBackdrop : Microsoft.UI.Xaml.Controls.UserControl
{
    public volatile bool Reduced;
    readonly CanvasAnimatedControl canvas=new(){Paused=true};
    CanvasBitmap? current,next;float blend=1;Color tint=Color.FromArgb(255,90,129,114);
    string? pending;bool hasPending,loading;
    public AmbientBackdrop(){Content=canvas;canvas.Draw+=Draw;canvas.Unloaded+=(_,_)=>canvas.Paused=true;}
    public void SetTint(Color c)=>tint=c;
    public void SetSource(string? path){pending=path;hasPending=true;canvas.Paused=false;}
    void StartLoad(ICanvasAnimatedControl sender,string? path)
    {
        _=LoadAsync(sender,path);
        async Task LoadAsync(ICanvasAnimatedControl s,string? p)
        {
            CanvasBitmap? bmp=null;
            try{
                if(p!=null&&File.Exists(p)){
                    try{bmp=await CanvasBitmap.LoadAsync(s,p);}
                    catch{using var ms=new MemoryStream(await File.ReadAllBytesAsync(p));using var ras=ms.AsRandomAccessStream();bmp=await CanvasBitmap.LoadAsync(s,ras);}
                }
            }catch(Exception e){Services.AppPaths.Log("Ambient load: "+e.Message);}
            DispatcherQueue.TryEnqueue(()=>{
                loading=false;
                if(Reduced||current==null){current=bmp;next=null;blend=1;}
                else{next=bmp;blend=0;}
            });
        }
    }
    void Draw(ICanvasAnimatedControl sender,CanvasAnimatedDrawEventArgs args)
    {
        var d=args.DrawingSession;float w=(float)sender.Size.Width,h=(float)sender.Size.Height;if(w<=0||h<=0){canvas.Paused=true;return;}
        d.Clear(Color.FromArgb(255,10,15,14));
        if(!loading&&hasPending){hasPending=false;loading=true;StartLoad(sender,pending);}
        if(loading||hasPending){
            if(current!=null)Layer(d,current,1,w,h);else Placeholder(d,w,h);
        }else if(next==null){
            if(current!=null)Layer(d,current,1,w,h);else Placeholder(d,w,h);
            canvas.Paused=true;return;
        }else{
            if(current!=null)Layer(d,current,1,w,h);
            blend+=(float)args.Timing.ElapsedTime.TotalSeconds*1.6f;
            if(blend>=1){current=next;next=null;blend=1;Layer(d,current,1,w,h);}
            else Layer(d,next,blend*blend*(3-2*blend),w,h);
        }
        d.FillRectangle(0,0,w,h,Color.FromArgb(52,6,10,9));
        using var shade=new CanvasLinearGradientBrush(sender,new[]{
            new CanvasGradientStop{Position=0,Color=Color.FromArgb(150,6,10,9)},
            new CanvasGradientStop{Position=.45f,Color=Color.FromArgb(96,6,10,9)},
            new CanvasGradientStop{Position=1,Color=Color.FromArgb(185,4,8,7)}}){StartPoint=new Vector2(0,0),EndPoint=new Vector2(0,h)};
        d.FillRectangle(0,0,w,h,shade);
    }
    void Placeholder(CanvasDrawingSession d,float w,float h)
    {
        float r=MathF.Max(w,h)*.95f;
        using var brush=new CanvasRadialGradientBrush(canvas,Color.FromArgb(200,40,62,52),Color.FromArgb(255,12,20,18)){Center=new Vector2(w*.32f,h*.42f),RadiusX=r,RadiusY=r};
        d.FillRectangle(0,0,w,h,brush);
        d.FillRectangle(0,0,w,h,Color.FromArgb(34,tint.R,tint.G,tint.B));
    }
    static void Layer(CanvasDrawingSession d,CanvasBitmap bmp,float alpha,float w,float h)
    {
        if(alpha<=0.004f)return;
        float iw=(float)bmp.Size.Width,ih=(float)bmp.Size.Height;if(iw<1||ih<1)return;
        // 放大 1.3 倍再铺满，让模糊采样不露出边缘。
        float scale=MathF.Max(w/iw,h/ih)*1.3f;
        using var blur=new GaussianBlurEffect{Source=bmp,BlurAmount=MathF.Max(60,w*.085f),BorderMode=EffectBorderMode.Hard,Optimization=EffectOptimization.Balanced};
        using var sat=new SaturationEffect{Source=blur,Saturation=1.15f};
        d.DrawImage(sat,new Rect((w-iw*scale)/2f,(h-ih*scale)/2f,iw*scale,ih*scale),new Rect(0,0,iw,ih),alpha*.82f);
    }
}
