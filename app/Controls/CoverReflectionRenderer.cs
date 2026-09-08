using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
namespace LumaMusic.Controls;

internal static class CoverReflectionRenderer
{
    public static async Task<string?> Render(string path)
    {
        string key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path+File.GetLastWriteTimeUtc(path).Ticks)))[..20];
        string file=Path.Combine(Services.AppPaths.Cache,"reflection-v1-"+key+".png");
        if(File.Exists(file))return file;
        var device=CanvasDevice.GetSharedDevice();using var image=await CanvasBitmap.LoadAsync(device,path);
        using var target=new CanvasRenderTarget(device,392,110,96);
        using(var ds=target.CreateDrawingSession()){
            ds.Clear(Color.FromArgb(0,0,0,0));
            using var blur=new GaussianBlurEffect{Source=image,BlurAmount=6,BorderMode=EffectBorderMode.Hard};
            ds.Transform=Matrix3x2.CreateScale(1,-1)*Matrix3x2.CreateTranslation(0,392);
            ds.DrawImage(blur,new Rect(0,0,392,392),new Rect(0,0,image.Size.Width,image.Size.Height));
        }
        var pixels=target.GetPixelBytes();
        for(int y=0;y<110;y++){float alpha=.86f*(1-y/109f);for(int x=0;x<392;x++)for(int channel=0;channel<4;channel++){int index=(y*392+x)*4+channel;pixels[index]=(byte)(pixels[index]*alpha);}}
        target.SetPixelBytes(pixels);
        await target.SaveAsync(file,CanvasBitmapFileFormat.Png);return file;
    }
}
