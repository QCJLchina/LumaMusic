using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;
namespace LumaMusic.Controls;
// 全局氛围背景：封面离线渲染成「高斯模糊+饱和+压暗」的 PNG（按内容缓存），
// 用普通 Image 层展示而不是交换链——这样页面上所有 AcrylicBrush 都能真实采样到背景，玻璃效果才成立。
public sealed class AmbientBackdrop : UserControl
{
    public bool Reduced {get;set;}
    static readonly SemaphoreSlim genGate=new(1,1);
    readonly Grid root=new();
    readonly Image shown=new(){Stretch=Stretch.UniformToFill};
    readonly Image entering=new(){Stretch=Stretch.UniformToFill,Opacity=0};
    string? lastPath;
    public AmbientBackdrop(){Content=root;root.Children.Add(shown);root.Children.Add(entering);}
    // 无封面时的占位：按封面平均色调的深色渐变。
    public void SetTint(Color c)=>root.Background=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(0,1),GradientStops=new GradientStopCollection{
        new GradientStop{Offset=0,Color=Color.FromArgb(255,(byte)(c.R*38/100),(byte)(c.G*38/100),(byte)(c.B*38/100))},
        new GradientStop{Offset=1,Color=Color.FromArgb(255,11,17,15)}}};
    public void SetSource(string? path){if(path!=lastPath){lastPath=path;_=Load(path);}}
    async Task Load(string? path)
    {
        string? file=null;
        try{if(!string.IsNullOrEmpty(path)&&File.Exists(path))file=await RenderBlurredAsync(path);}
        catch(Exception e){Services.AppPaths.Log("Ambient: "+e.Message);}
        if(file==null){shown.Source=null;entering.Source=null;return;}
        ImageSource src=new BitmapImage(new Uri(file));
        if(Reduced){shown.Source=src;entering.Opacity=0;return;}
        entering.Source=src;
        var sb=new Storyboard();
        var anim=new DoubleAnimation{From=0,To=1,Duration=new Duration(TimeSpan.FromMilliseconds(750)),EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}};
        Storyboard.SetTarget(anim,entering);Storyboard.SetTargetProperty(anim,"Opacity");
        sb.Children.Add(anim);
        sb.Completed+=(_,_)=>{shown.Source=entering.Source;entering.Source=null;entering.Opacity=0;};
        sb.Begin();
    }
    static async Task<string?> RenderBlurredAsync(string coverPath)
    {
        var key=Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(coverPath.ToLowerInvariant())))[..16].ToLowerInvariant();
        var outFile=System.IO.Path.Combine(Services.AppPaths.Cache,"ambient-"+key+".png");
        if(File.Exists(outFile))return outFile;
        await genGate.WaitAsync();
        try{
            if(File.Exists(outFile))return outFile;
            var device=CanvasDevice.GetSharedDevice();
            using var bmp=await CanvasBitmap.LoadAsync(device,coverPath);
            float w=1920,h=1200;
            using var target=new CanvasRenderTarget(device,w,h,96);
            using(var ds=target.CreateDrawingSession()){
                ds.Clear(Color.FromArgb(255,12,18,16));
                float iw=(float)bmp.Size.Width,ih=(float)bmp.Size.Height;
                // 放大 1.25 倍铺满，模糊采样不露边缘。
                float scale=MathF.Max(w/iw,h/ih)*1.25f;
                using var blur=new GaussianBlurEffect{Source=bmp,BlurAmount=MathF.Max(70,w*.06f),BorderMode=EffectBorderMode.Hard,Optimization=EffectOptimization.Balanced};
                using var sat=new SaturationEffect{Source=blur,Saturation=1.18f};
                ds.DrawImage(sat,new Rect((w-iw*scale)/2f,(h-ih*scale)/2f,iw*scale,ih*scale),new Rect(0,0,iw,ih),0.85f);
                // 压暗直接烘进图里：顶部保标题、中部适中、底部最深保播放条对比度。
                using var shade=new CanvasLinearGradientBrush(device,new[]{
                    new CanvasGradientStop{Position=0,Color=Color.FromArgb(140,6,10,9)},
                    new CanvasGradientStop{Position=.45f,Color=Color.FromArgb(88,6,10,9)},
                    new CanvasGradientStop{Position=1,Color=Color.FromArgb(175,4,8,7)}}){StartPoint=new Vector2(0,0),EndPoint=new Vector2(0,h)};
                ds.FillRectangle(0,0,w,h,shade);
            }
            var pixels=target.GetPixelBytes();
            using var ras=new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,ras);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,(uint)w,(uint)h,96,96,pixels);
            await encoder.FlushAsync();
            var size=(uint)ras.Size;
            using var reader=new Windows.Storage.Streams.DataReader(ras.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var png=new byte[size];reader.ReadBytes(png);
            File.WriteAllBytes(outFile,png);
            return outFile;
        }catch(Exception e){Services.AppPaths.Log("Ambient render: "+e.Message);return null;}
        finally{genGate.Release();}
    }
}
