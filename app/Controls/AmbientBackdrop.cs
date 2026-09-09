using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;
namespace LumaMusic.Controls;

// Three independent compositor fields. Only palette extraction and the glass
// sampling image are rendered off-thread; there is no per-frame UI timer.
public sealed class AmbientBackdrop : UserControl
{
    readonly Grid root=new();
    readonly Grid host=new(){IsHitTestVisible=false};
    readonly SpriteVisual[] blobs=new SpriteVisual[3];
    readonly CompositionRadialGradientBrush[] gradients=new CompositionRadialGradientBrush[3];
    readonly CompositionPropertySet?[] motions=new CompositionPropertySet?[3];
    readonly CompositionEffectBrush[] effects=new CompositionEffectBrush[3];
    ContainerVisual? scene;
    readonly Microsoft.UI.Xaml.Shapes.Rectangle vignette=new(){IsHitTestVisible=false};
    bool reduced,aurora=true,playing,running;
    bool pageVisible;
    public void SetPageVisible(bool value){pageVisible=value;ApplyAurora();}
    internal bool MotionRunning=>running;
    internal int BlobCount=>quality==1?2:3;
    internal bool Playing=>playing;
    internal Color[] CurrentPalette=>palette.ToArray();
    int quality=2,generation;
    string themeMode="dark";
    string? lastPath;
    Color[] palette=Fallback();
    static readonly SemaphoreSlim genGate=new(1,1);
    public event Action<string?>? BackgroundChanged;
    public event Action<Color[]>? PaletteChanged;
    internal string? CurrentBackgroundPath {get;private set;}
    public bool Active {get;private set;}=true;
    public bool Reduced {get=>reduced;set{if(reduced==value)return;reduced=value;ApplyAurora();}}
    public bool Aurora {get=>aurora;set{if(aurora==value)return;aurora=value;ApplyAurora();}}
    public int Quality {get=>quality;set{value=Math.Clamp(value,1,2);if(quality==value)return;quality=value;running=false;ApplyAurora();}}
    public string ThemeMode {get=>themeMode;set{if(themeMode==value)return;themeMode=value;SetTint(default);ApplyPalette();ApplyState();_=Load(lastPath);}}
    static Color[] Fallback()=>[Color.FromArgb(255,154,144,72),Color.FromArgb(255,47,74,53),Color.FromArgb(255,224,205,122)];
    public AmbientBackdrop()
    {
        Content=root;root.Children.Add(host);SetTint(default);
        root.Children.Add(vignette);
        root.Children.Add(new Image{Source=new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/AmbientNoise.png")),Stretch=Stretch.UniformToFill,Opacity=.008,IsHitTestVisible=false});
        Loaded+=(_,_)=>{InitializeScene();ApplyAurora();};
        Unloaded+=(_,_)=>SetActive(false);
        SizeChanged+=(_,e)=>{root.Clip=new RectangleGeometry{Rect=new(0,0,e.NewSize.Width,e.NewSize.Height)};LayoutScene();};
    }
    public void SetTint(Color color){bool light=ThemeMode=="light";root.Background=new SolidColorBrush(light?Color.FromArgb(255,242,239,233):Color.FromArgb(255,20,17,13));var brush=new RadialGradientBrush{Center=new(.5,.4),RadiusX=.75,RadiusY=.8};brush.GradientStops.Add(new(){Offset=.3,Color=Color.FromArgb(0,10,8,6)});brush.GradientStops.Add(new(){Offset=1,Color=light?Color.FromArgb(120,255,252,245):Color.FromArgb(140,10,8,6)});vignette.Fill=brush;}
    public void SetActive(bool active){if(Active==active)return;Active=active;ApplyAurora();}
    public void SetPlaying(bool value){if(playing==value)return;playing=value;ApplyPalette();ApplyState();}
    public void Shutdown(){generation++;SetActive(false);foreach(var b in effects)b?.Dispose();foreach(var m in motions)m?.Dispose();}
    void InitializeScene()
    {
        if(scene!=null)return;var c=ElementCompositionPreview.GetElementVisual(host).Compositor;scene=c.CreateContainerVisual();ElementCompositionPreview.SetElementChildVisual(host,scene);
        for(int i=0;i<3;i++){
            var g=c.CreateRadialGradientBrush();g.EllipseCenter=new(.5f,.5f);g.EllipseRadius=new(.34f,.34f);
            g.ColorStops.Add(c.CreateColorGradientStop(0,palette[i]));g.ColorStops.Add(c.CreateColorGradientStop(.45f,Color.FromArgb(100,palette[i].R,palette[i].G,palette[i].B)));g.ColorStops.Add(c.CreateColorGradientStop(1,Color.FromArgb(0,palette[i].R,palette[i].G,palette[i].B)));gradients[i]=g;
            using var effect=new GaussianBlurEffect{Name="Blur",BlurAmount=27.5f,BorderMode=EffectBorderMode.Soft,Source=new CompositionEffectSourceParameter("Source")};
            using var factory=c.CreateEffectFactory(effect,["Blur.BlurAmount"]);var brush=factory.CreateBrush();brush.SetSourceParameter("Source",g);effects[i]=brush;
            var blob=c.CreateSpriteVisual();blob.Brush=brush;blobs[i]=blob;scene.Children.InsertAtTop(blob);
        }
        LayoutScene();ApplyPalette();
    }
    void LayoutScene()
    {
        if(scene==null||ActualWidth<=0||ActualHeight<=0)return;
        float w=(float)ActualWidth/4,h=(float)ActualHeight/4;scene.Size=new(w,h);scene.CenterPoint=new(w/2,h/2,0);scene.Offset=new(w*1.5f,h*1.5f,0);scene.Scale=new(4,4,1);
        float[] widths=[.74f,.58f,.36f],heights=[.78f,.66f,.44f];Vector2[] positions=[new(-.12f,-.12f),new(.55f,.45f),new(.42f,.12f)];
        const float pad=105; // Three blur radii of transparent guard pixels at quarter resolution.
        for(int i=0;i<3;i++){blobs[i].Size=new(w*widths[i]+2*pad,h*heights[i]+2*pad);gradients[i].EllipseRadius=new(w*widths[i]/(2*blobs[i].Size.X),h*heights[i]/(2*blobs[i].Size.Y));blobs[i].CenterPoint=new(blobs[i].Size/2,0);blobs[i].Offset=new(w*positions[i].X-pad,h*positions[i].Y-pad,0);}
        running=false;ApplyAurora();
    }
    public void ApplyAurora()
    {
        if(scene==null)return;host.Visibility=Aurora?Visibility.Visible:Visibility.Collapsed;
        bool animate=Active&&pageVisible&&Aurora&&!Reduced;
        for(int i=0;i<3;i++){
            var v=blobs[i];v.IsVisible=i<(quality==1?2:3);
            if(!animate||!v.IsVisible){motions[i]?.StopAnimation("Position");v.StopAnimation("TransformMatrix");v.StopAnimation("Scale");v.StopAnimation("Opacity");v.TransformMatrix=Matrix4x4.Identity;v.Scale=Vector3.One;v.Opacity=.77f;}
        }
        if(animate&&!running)StartMotion();running=animate;if(!animate){scene.StopAnimation("Scale");scene.Scale=new(4,4,1);}ApplyState();
    }
    void StartMotion()
    {
        double[] drift=[38,46,27],breath=[9,11,7.5];float[] phase=[0,3f/11,5f/7.5f];
        for(int i=0;i<(quality==1?2:3);i++){
            var v=blobs[i];var c=v.Compositor;var move=c.CreateVector3KeyFrameAnimation();
            // Offset is layout-owned. TransformMatrix owns drift; Scale owns breathing.
            var matrix=c.CreateExpressionAnimation("Matrix4x4.CreateTranslation(motion.Position)");motions[i]?.StopAnimation("Position");motions[i]?.Dispose();var motion=c.CreatePropertySet();motions[i]=motion;motion.InsertVector3("Position",Vector3.Zero);matrix.SetReferenceParameter("motion",motion);
            var ease=c.CreateCubicBezierEasingFunction(new(.42f,0),new(.58f,1));float dx=(float)ActualWidth/4*(i==0?.09f:-.08f),dy=(float)ActualHeight/4*.07f;
            move.InsertKeyFrame(0,new(-dx,-dy,0));move.InsertKeyFrame(.5f,new(dx,dy,0),ease);move.InsertKeyFrame(1,new(-dx,-dy,0),ease);move.Duration=TimeSpan.FromSeconds(drift[i]*2);move.IterationBehavior=AnimationIterationBehavior.Forever;motion.StartAnimation("Position",move);v.StartAnimation("TransformMatrix",matrix);
            var scale=c.CreateVector3KeyFrameAnimation();var opacity=c.CreateScalarKeyFrameAnimation();
            for(int k=0;k<=12;k++){float t=k/12f,wave=(1+MathF.Sin((t+phase[i])*MathF.PI*2))/2;float z=.92f+.2f*wave;scale.InsertKeyFrame(t,new(z,z,1));opacity.InsertKeyFrame(t,.62f+.3f*wave);}
            scale.Duration=opacity.Duration=TimeSpan.FromSeconds(breath[i]);scale.IterationBehavior=opacity.IterationBehavior=AnimationIterationBehavior.Forever;v.StartAnimation("Scale",scale);v.StartAnimation("Opacity",opacity);
        }
    }
    void ApplyState()
    {
        if(scene==null)return;float target=(ThemeMode=="light"?.35f:1)*(playing?1:.42f*.78f);
        var a=scene.Compositor.CreateScalarKeyFrameAnimation();a.InsertKeyFrame(1,target);a.Duration=TimeSpan.FromSeconds(1.4);
        if(Reduced||!Active){scene.StopAnimation("Opacity");scene.Opacity=target;}else scene.StartAnimation("Opacity",a);
        // Keep the dark playback field open at the edges, without adding another light layer.
        var edge=ElementCompositionPreview.GetElementVisual(vignette);
        float edgeOpacity=ThemeMode=="dark"&&playing?.65f:1;
        if(Reduced||!Active){edge.StopAnimation("Opacity");edge.Opacity=edgeOpacity;}
        else {using var fade=scene.Compositor.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(1,edgeOpacity);fade.Duration=TimeSpan.FromSeconds(1.4);edge.StartAnimation("Opacity",fade);}
        foreach(var brush in effects){float blur=(quality==1?80:playing?110:140)/4f;var b=brush.Compositor.CreateScalarKeyFrameAnimation();b.InsertKeyFrame(1,blur);b.Duration=TimeSpan.FromSeconds(1.4);if(Reduced||!Active){brush.StopAnimation("Blur.BlurAmount");brush.Properties.InsertScalar("Blur.BlurAmount",blur);}else brush.StartAnimation("Blur.BlurAmount",b);}
    }
    public void Burst()
    {
        if(scene==null||Reduced||!Active||!pageVisible||!Aurora)return;var c=scene.Compositor;foreach(var brush in effects){var blur=c.CreateScalarKeyFrameAnimation();blur.InsertKeyFrame(0,17.5f);blur.InsertKeyFrame(1,(quality==1?80:playing?110:140)/4f);blur.Duration=TimeSpan.FromSeconds(.95);brush.StartAnimation("Blur.BlurAmount",blur);}
        var a=c.CreateVector3KeyFrameAnimation();a.InsertKeyFrame(0,new(3.44f,3.44f,1));a.InsertKeyFrame(1,new(4,4,1),c.CreateCubicBezierEasingFunction(new(.16f,1),new(.3f,1)));a.Duration=TimeSpan.FromSeconds(.95);scene.StartAnimation("Scale",a);
    }
    void ApplyPalette()
    {
        if(scene==null)return;
        bool illuminate=ThemeMode=="dark"&&playing;
        for(int i=0;i<3;i++)for(int k=0;k<3;k++){
            var stop=gradients[i].ColorStops[k];var color=palette[i];
            // Lift the displayed field only: the extracted palette and UI accent stay intact.
            if(illuminate){
                static byte Lift(byte channel)=>(byte)Math.Round(channel+(255-channel)*.12);
                color.R=Lift(color.R);color.G=Lift(color.G);color.B=Lift(color.B);
            }
            color.A=k==0?(byte)255:k==1?(illuminate?(byte)145:(byte)100):(byte)0;
            using var a=scene.Compositor.CreateColorKeyFrameAnimation();a.InsertKeyFrame(1,color);a.Duration=TimeSpan.FromSeconds(1.2);
            if(Reduced||!Active){stop.StopAnimation("Color");stop.Color=color;}else stop.StartAnimation("Color",a);
        }
    }
    public void SetSource(string? path){if(path==lastPath)return;lastPath=path;_=Load(path);}
    async Task Load(string? path)
    {
        int request=++generation;string mode=$"{ThemeMode}:{quality}";string? file=null;
        try{if(!string.IsNullOrEmpty(path)&&File.Exists(path))file=await Task.Run(()=>RenderBlurredAsync(path,mode));}catch(Exception e){Services.AppPaths.Log("Ambient: "+e.Message);}
        if(request!=generation)return;palette=file==null?Fallback():LoadColors(file);if(palette.Length<3)palette=Fallback();CurrentBackgroundPath=file;ApplyPalette();PaletteChanged?.Invoke(palette);BackgroundChanged?.Invoke(file);
    }
    // ---------- 离线渲染 ----------
    static async Task<string?> RenderBlurredAsync(string coverPath,string mode)
    {
        bool light=mode.StartsWith("light",StringComparison.Ordinal);
        var key=(light?"v6l-":"v6d-")+Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(coverPath.ToLowerInvariant()+File.GetLastWriteTimeUtc(coverPath).Ticks)))[..16].ToLowerInvariant();
        var outFile=System.IO.Path.Combine(Services.AppPaths.Cache,"ambient-"+key+".png");
        if(File.Exists(outFile))return outFile;
        await genGate.WaitAsync();
        try{
            if(File.Exists(outFile))return outFile;
            var device=CanvasDevice.GetSharedDevice();
            using var bmp=await CanvasBitmap.LoadAsync(device,coverPath);
            float w=mode.EndsWith(":1",StringComparison.Ordinal)?640:1280;
            float h=mode.EndsWith(":1",StringComparison.Ordinal)?400:800;
            using var target=new CanvasRenderTarget(device,w,h,96);
            using(var ds=target.CreateDrawingSession()){
                ds.Clear(light?Color.FromArgb(255,243,243,246):Color.FromArgb(255,24,25,30));
                float iw=(float)bmp.Size.Width,ih=(float)bmp.Size.Height;
                // 放大 1.25 倍铺满，模糊采样不露边缘。
                float scale=MathF.Max(w/iw,h/ih)*1.25f;
                // 浅色版要真正提亮：封面压低不透明度让白底透出，再叠更强的白色渐变。
                 using var blur=new GaussianBlurEffect{Source=bmp,BlurAmount=MathF.Max(18,w*.018f),BorderMode=EffectBorderMode.Hard,Optimization=EffectOptimization.Balanced};
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
            var buckets=new Dictionary<int,List<(double h,double s,double l)>>();
            for(int i=0;i<px.Length;i+=4){if(px[i+3]<128)continue;double r=px[i+2]/255d,g=px[i+1]/255d,b=px[i]/255d;double hi=Math.Max(r,Math.Max(g,b)),lo=Math.Min(r,Math.Min(g,b)),d=hi-lo,l=(hi+lo)/2;if(d<.07||l<.06||l>.94)continue;double sat=d/(1-Math.Abs(2*l-1));double hue=(hi==r?(g-b)/d+(g<b?6:0):hi==g?(b-r)/d+2:(r-g)/d+4)/6;int key=(int)(hue*12)*3+(int)(l*3);if(!buckets.TryGetValue(key,out var list))buckets[key]=list=[];list.Add((hue,sat,l));}
            var clusters=buckets.Values.OrderByDescending(x=>x.Count).Take(3).Select(x=>(h:x.Average(v=>v.h),s:x.Average(v=>v.s),l:x.Average(v=>v.l))).ToList();
            if(clusters.Count==0)return Fallback();
            var main=clusters.OrderByDescending(x=>x.s*(1-Math.Abs(x.l-.45))).First();var dark=clusters.MinBy(x=>x.l);var bright=clusters.MaxBy(x=>x.l);
            return [Hsl(main.h,main.s,main.l),Hsl(dark.h,dark.s,dark.l),Hsl(bright.h,bright.s,bright.l*1.2)];
        }catch{return Fallback();}
    }
    static Color Hsl(double h,double s,double l)
    {
        s=Math.Clamp(s,.35,.75);l=Math.Clamp(l,.22,.62);double a=s*Math.Min(l,1-l);
        byte Channel(double n){double k=(n+h*12)%12;return (byte)Math.Round(255*(l-a*Math.Max(-1,Math.Min(k-3,Math.Min(9-k,1)))));}
        return Color.FromArgb(255,Channel(0),Channel(8),Channel(4));
    }
}
