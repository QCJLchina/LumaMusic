using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;
namespace LumaMusic.Controls;
// 全局氛围背景：封面离线渲染成「高斯模糊+饱和+压暗/提亮」的 PNG（按内容与主题缓存），
// 用普通 Image 层展示——页面上所有 AcrylicBrush 都能真实采样到背景，玻璃效果才成立。
// 可选流光层：从封面提取的 3 个主色做成漂移色斑（XAML 层，同样可被玻璃采样）。
public sealed class AmbientBackdrop : UserControl
{
    bool reduced;
    public bool Reduced {get=>reduced;set{reduced=value;if(value){transition?.Stop();if(entering.Source!=null){shown.Source=entering.Source;entering.Source=null;}entering.Opacity=0;}ApplyAurora();}}
    public bool Active {get;private set;}=true;
    public void SetActive(bool active){Active=active;if(!active){transition?.Stop();if(entering.Source!=null){shown.Source=entering.Source;entering.Source=null;}entering.Opacity=0;}ApplyAurora();}
    int generation;
    Storyboard? transition;
    public void Shutdown(){++generation;transition?.Stop();Active=false;ApplyAurora();foreach(var animation in drift)animation?.Dispose();}
    public event Action<string?>? BackgroundChanged;
    internal string? CurrentBackgroundPath {get;private set;}
    public bool Aurora {get;set;}=true;
    string themeMode="dark";
    public string ThemeMode{get=>themeMode;set{if(themeMode==value)return;themeMode=value;SetTint(Windows.UI.Color.FromArgb(255,115,115,130));_=Load(lastPath);}}
    static readonly SemaphoreSlim genGate=new(1,1);
    readonly Grid root=new();
    readonly Image shown=new(){Stretch=Stretch.UniformToFill};
    readonly Image entering=new(){Stretch=Stretch.UniformToFill,Opacity=0};
    readonly Canvas aurora=new(){IsHitTestVisible=false,Opacity=0.42};
    readonly Ellipse[] blobs=new Ellipse[4];
    readonly Microsoft.UI.Composition.Vector3KeyFrameAnimation?[] drift=new Microsoft.UI.Composition.Vector3KeyFrameAnimation?[4];
    Color[] palette=Array.Empty<Color>();
    string? lastPath;
    public AmbientBackdrop()
    {
        SetTint(Color.FromArgb(255,115,115,130));
        Content=root;root.Children.Add(shown);root.Children.Add(entering);root.Children.Add(aurora);
        for(int i=0;i<blobs.Length;i++){
            blobs[i]=new Ellipse{Opacity=i==0?0.92f:0.64f};aurora.Children.Add(blobs[i]);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(blobs[i],true);
        }
        Unloaded+=(_,_)=>SetActive(false);Loaded+=(_,_)=>SetActive(true);
        root.SizeChanged+=(_,e)=>{root.Clip=new RectangleGeometry{Rect=new Rect(0,0,e.NewSize.Width,e.NewSize.Height)};LayoutBlobs();};
    }
    // 无封面时的占位：按封面平均色调的渐变（浅色主题用亮版）。
    public void SetTint(Color c)
    {
        var top=ThemeMode=="light"?Color.FromArgb(255,(byte)Math.Min(255,c.R*55/100+120),(byte)Math.Min(255,c.G*55/100+120),(byte)Math.Min(255,c.B*55/100+120)):Color.FromArgb(255,(byte)(c.R*38/100),(byte)(c.G*38/100),(byte)(c.B*38/100));
        var bottom=ThemeMode=="light"?Color.FromArgb(255,243,243,246):Color.FromArgb(255,24,25,30);
        root.Background=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(0,1),GradientStops=new GradientStopCollection{
            new GradientStop{Offset=0,Color=top},new GradientStop{Offset=1,Color=bottom}}};
    }
    public event Action<Color[]>? PaletteChanged;
    public void SetSource(string? path){if(path!=lastPath){lastPath=path;_=Load(path);}}
    async Task Load(string? path)
    {
        int request=++generation;
        string mode=ThemeMode;
        transition?.Stop();
        if(entering.Source!=null){shown.Source=entering.Source;entering.Source=null;}entering.Opacity=0;
        string? file=null;
        try{if(!string.IsNullOrEmpty(path)&&File.Exists(path))file=await RenderBlurredAsync(path,mode);}
        catch(Exception e){Services.AppPaths.Log("Ambient: "+e.Message);}
        if(request!=generation)return;
        palette=file==null?[]:LoadColors(file);
        CurrentBackgroundPath=file;
        ApplyAuroraColors();PaletteChanged?.Invoke(palette);BackgroundChanged?.Invoke(file);
        if(file==null){shown.Source=null;entering.Source=null;return;}
        ImageSource src=new BitmapImage(new Uri(file));
        if(Reduced||!Active){shown.Source=src;entering.Source=null;entering.Opacity=0;return;}
        entering.Source=src;
        var sb=new Storyboard();transition=sb;
        var anim=new DoubleAnimation{From=0,To=1,Duration=new Duration(TimeSpan.FromMilliseconds(600)),EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}};
        Storyboard.SetTarget(anim,entering);Storyboard.SetTargetProperty(anim,"Opacity");sb.Children.Add(anim);
        sb.Completed+=(_,_)=>{if(request!=generation)return;shown.Source=src;entering.Source=null;entering.Opacity=0;};
        sb.Begin();
    }
    // ---------- 流光 ----------
    public void ApplyAurora()
    {
        aurora.Visibility=Aurora&&!Reduced&&Active&&palette.Length>0?Visibility.Visible:Visibility.Collapsed;
        if(Aurora&&!Reduced&&Active&&palette.Length>0)StartDrift();else foreach(var blob in blobs){var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(blob);visual.StopAnimation("Translation");visual.StopAnimation("Scale");visual.StopAnimation("Opacity");}
    }
    void ApplyAuroraColors()
    {
        if(palette.Length==0){ApplyAurora();return;}
        for(int i=0;i<blobs.Length;i++){
            var c=palette[i%palette.Length];
            var brush=new RadialGradientBrush{Center=new Point(.5,.5),RadiusX=.5,RadiusY=.5};
            brush.GradientStops.Add(new GradientStop{Offset=0,Color=Color.FromArgb(i==0?(byte)185:(byte)145,c.R,c.G,c.B)});
            brush.GradientStops.Add(new GradientStop{Offset=1,Color=Color.FromArgb(0,c.R,c.G,c.B)});
            blobs[i].Fill=brush;
        }
        ApplyAurora();
    }
    void LayoutBlobs()
    {
        double w=root.ActualWidth,h=root.ActualHeight;if(w<=0||h<=0)return;
        double extent=Math.Max(w,h);double[] sizes={.82,.68,.92,.60};Point[] bases={new(-.12,-.08),new(.55,-.18),new(.28,.48),new(.72,.52)};
        for(int i=0;i<blobs.Length;i++){
            blobs[i].Width=blobs[i].Height=extent*sizes[i];
            Canvas.SetLeft(blobs[i],w*bases[i].X);Canvas.SetTop(blobs[i],h*bases[i].Y);
        }
        StartDrift();
    }
    void StartDrift()
    {
        if(!Aurora||Reduced||!Active||palette.Length==0||root.ActualWidth<=0)return;
        for(int i=0;i<blobs.Length;i++){
            var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(blobs[i]);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(blobs[i],true);
            visual.StopAnimation("Translation");drift[i]?.Dispose();
            var animation=visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0,Vector3.Zero);
            animation.InsertKeyFrame(.28f,new Vector3((float)(root.ActualWidth*.12*(i%2==0?1:-1)),(float)(root.ActualHeight*.08*(i<2?1:-1)),0));
            animation.InsertKeyFrame(.62f,new Vector3((float)(root.ActualWidth*.07*(i%2==0?-1:1)),(float)(root.ActualHeight*.13*(i<2?-1:1)),0));
            animation.InsertKeyFrame(1,Vector3.Zero);animation.Duration=TimeSpan.FromSeconds(24+i*7);
            animation.IterationBehavior=Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            visual.CenterPoint=new Vector3((float)(blobs[i].ActualWidth/2),(float)(blobs[i].ActualHeight/2),0);
            var breathe=visual.Compositor.CreateVector3KeyFrameAnimation();breathe.InsertKeyFrame(0,Vector3.One);breathe.InsertKeyFrame(.5f,new Vector3(1.12f-i*.015f,1.08f+i*.01f,1));breathe.InsertKeyFrame(1,Vector3.One);breathe.Duration=TimeSpan.FromSeconds(18+i*5);breathe.IterationBehavior=Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            var shimmer=visual.Compositor.CreateScalarKeyFrameAnimation();shimmer.InsertKeyFrame(0,i==0?.72f:.46f);shimmer.InsertKeyFrame(.5f,i==0?.96f:.70f);shimmer.InsertKeyFrame(1,i==0?.72f:.46f);shimmer.Duration=TimeSpan.FromSeconds(15+i*4);shimmer.IterationBehavior=Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            drift[i]=animation;visual.StartAnimation("Translation",animation);visual.StartAnimation("Scale",breathe);visual.StartAnimation("Opacity",shimmer);
        }
    }
    // ---------- 离线渲染 ----------
    static async Task<string?> RenderBlurredAsync(string coverPath,string mode)
    {
        bool light=mode=="light";
        var key=(light?"v5l-":"v5d-")+Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(coverPath.ToLowerInvariant()+File.GetLastWriteTimeUtc(coverPath).Ticks)))[..16].ToLowerInvariant();
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
                ds.Clear(light?Color.FromArgb(255,243,243,246):Color.FromArgb(255,24,25,30));
                float iw=(float)bmp.Size.Width,ih=(float)bmp.Size.Height;
                // 放大 1.25 倍铺满，模糊采样不露边缘。
                float scale=MathF.Max(w/iw,h/ih)*1.25f;
                // 浅色版要真正提亮：封面压低不透明度让白底透出，再叠更强的白色渐变。
                using var blur=new GaussianBlurEffect{Source=bmp,BlurAmount=MathF.Max(28,w*.024f),BorderMode=EffectBorderMode.Hard,Optimization=EffectOptimization.Balanced};
                using var sat=new SaturationEffect{Source=blur,Saturation=.82f};
                ds.DrawImage(sat,new Rect((w-iw*scale)/2f,(h-ih*scale)/2f,iw*scale,ih*scale),new Rect(0,0,iw,ih),light?0.46f:0.62f);
                CanvasGradientStop[] stops=light?new[]{
                    new CanvasGradientStop{Position=0,Color=Color.FromArgb(160,255,255,255)},
                    new CanvasGradientStop{Position=.45f,Color=Color.FromArgb(115,255,255,255)},
                    new CanvasGradientStop{Position=1,Color=Color.FromArgb(185,245,245,248)}}:new[]{
                    new CanvasGradientStop{Position=0,Color=Color.FromArgb(95,18,19,24)},
                    new CanvasGradientStop{Position=.45f,Color=Color.FromArgb(55,18,19,24)},
                    new CanvasGradientStop{Position=1,Color=Color.FromArgb(125,20,21,26)}};
                using var shade=new CanvasLinearGradientBrush(device,stops){StartPoint=new Vector2(0,0),EndPoint=new Vector2(0,h)};
                ds.FillRectangle(0,0,w,h,shade);
            }
            var pixels=target.GetPixelBytes();
            // 32x32 降采样提取 4 象限主色，驱动流光层。
            var colors=ExtractColors(device,bmp);
            using var ras=new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,ras);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,(uint)w,(uint)h,96,96,pixels);
            await encoder.FlushAsync();
            var size=(uint)ras.Size;
            using var reader=new Windows.Storage.Streams.DataReader(ras.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var png=new byte[size];reader.ReadBytes(png);
            File.WriteAllBytes(outFile,png);
            try{File.WriteAllBytes(outFile+".colors",ExtractColors(device,bmp).SelectMany(c=>new byte[]{c.R,c.G,c.B}).ToArray());}catch{}
            return outFile;
        }catch(Exception e){Services.AppPaths.Log("Ambient render: "+e.Message);return null;}
        finally{genGate.Release();}
    }
    // 色板随 PNG 存 sidecar 文件，缓存命中时也能恢复流光配色。
    static Color[] LoadColors(string outFile)
    {
        try{
            var f=outFile+".colors";if(!File.Exists(f))return [];
            var bytes=File.ReadAllBytes(f);
            var list=new List<Color>();
            for(int i=0;i+2<bytes.Length;i+=3)list.Add(Color.FromArgb(255,bytes[i],bytes[i+1],bytes[i+2]));
            return list.ToArray();
        }catch{return [];}
    }
    static Color[] ExtractColors(CanvasDevice device,CanvasBitmap bmp)
    {
        try{
            using var small=new CanvasRenderTarget(device,32,32,96);
            using(var ds=small.CreateDrawingSession())ds.DrawImage(bmp,new Rect(0,0,32,32),new Rect(0,0,bmp.Size.Width,bmp.Size.Height));
            var px=small.GetPixelBytes();
            // 四象限均色，跳过过暗/过亮的角，取前三个差异最大的。
            var quads=new List<Color>();
            for(int qy=0;qy<2;qy++)for(int qx=0;qx<2;qx++){
                long r=0,g=0,b=0;int n=0;
                for(int y=qy*16;y<(qy+1)*16;y+=2)for(int x=qx*16;x<(qx+1)*16;x+=2){
                    var off=(y*32+x)*4;b+=px[off];g+=px[off+1];r+=px[off+2];n++;
                }
                if(n==0)continue;
                quads.Add(Color.FromArgb(255,(byte)(r/n),(byte)(g/n),(byte)(b/n)));
            }
            var picked=new List<Color>();
            foreach(var c in quads.OrderByDescending(c=>(c.R+c.G+c.B))){
                if(picked.All(p=>Math.Abs(p.R-c.R)+Math.Abs(p.G-c.G)+Math.Abs(p.B-c.B)>60))picked.Add(c);
                if(picked.Count==3)break;
            }
            while(picked.Count<3)picked.Add(picked.Count>0?picked[0]:Color.FromArgb(255,90,129,114));
            return picked.ToArray();
        }catch{return [];}
    }
}
