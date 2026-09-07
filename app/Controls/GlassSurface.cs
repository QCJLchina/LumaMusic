using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace LumaMusic.Controls;

public enum GlassPreset { Navigation, Player, Overlay }

/// <summary>In-app acrylic with a narrow, magnified cover-background rim. Content is never distorted.</summary>
[ContentProperty(Name = nameof(SurfaceContent))]
public sealed class GlassSurface : UserControl
{
    public static readonly DependencyProperty SurfaceContentProperty = DependencyProperty.Register(
        nameof(SurfaceContent), typeof(UIElement), typeof(GlassSurface),
        new PropertyMetadata(null, (d, e) => ((GlassSurface)d).presenter.Content = e.NewValue));
    public UIElement? SurfaceContent { get => (UIElement?)GetValue(SurfaceContentProperty); set => SetValue(SurfaceContentProperty, value); }
    public GlassPreset Preset { get; set; }
    readonly Grid layers = new();
    readonly Border material = new() { CornerRadius = new(26) };
    readonly Grid lensHost = new() { IsHitTestVisible = false };
    readonly Border rim = new() { CornerRadius = new(26), BorderThickness = new(1), IsHitTestVisible = false };
    readonly Border highlight = new() { CornerRadius = new(26), BorderThickness = new(1.5), IsHitTestVisible = false, Opacity = 0 };
    readonly ContentPresenter presenter = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    FrameworkElement? scene;
    LoadedImageSurface? source;
    SpriteVisual? lens;
    CompositionSurfaceBrush? lensBrush;
    CompositionGeometricClip? lensClip;
    CompositionPathGeometry? lensGeometry;
    bool reduced, motionReduced;
    internal bool UsesSolidMaterial => material.Background is SolidColorBrush;
    internal bool HasLens => lens != null;
    public GlassSurface()
    {
        Content = layers;
        layers.Children.Add(material); layers.Children.Add(lensHost); layers.Children.Add(rim);
        layers.Children.Add(highlight); layers.Children.Add(presenter);
        highlight.BorderBrush = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255));
        Loaded += (_, _) => { Refresh(); UpdateLens(); };
        Unloaded += (_, _) => ReleaseLens();
        SizeChanged += (_, _) => UpdateLens();
        ActualThemeChanged += (_, _) => Refresh();
        PointerEntered += (_, _) => Illuminate(.42f);
        PointerExited += (_, _) => Illuminate(0);
    }
    public void Configure(bool reduceTransparency, bool reduceMotion, bool highContrast)
    {
        reduced = reduceTransparency || highContrast; motionReduced = reduceMotion;
        HighContrast = highContrast; Refresh(); UpdateLens();
    }
    bool HighContrast;
    void Refresh()
    {
        bool light = ActualTheme == ElementTheme.Light;
        var tint = light ? Color.FromArgb(255, 250, 250, 252) : Color.FromArgb(255, 35, 36, 42);
        if (HighContrast)
        {
            material.Background = (Brush)Application.Current.Resources["SystemControlBackgroundAltHighBrush"];
            rim.BorderBrush = (Brush)Application.Current.Resources["SystemControlForegroundBaseHighBrush"];
        }
        else
        {
            material.Background = reduced ? new SolidColorBrush(tint) : new AcrylicBrush
            {
                TintColor = tint, TintOpacity = Preset == GlassPreset.Overlay ? .52 : Preset == GlassPreset.Player ? .22 : .30,
                TintLuminosityOpacity = light ? .64 : .38, FallbackColor = tint
            };
            rim.BorderBrush = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(1, 1),
                GradientStops = new()
                {
                    new() { Offset = 0, Color = Color.FromArgb(light ? (byte)230 : (byte)90, 255, 255, 255) },
                    new() { Offset = .45, Color = Color.FromArgb(12, 255, 255, 255) },
                    new() { Offset = 1, Color = Color.FromArgb(light ? (byte)45 : (byte)55, 140, 145, 160) }
                }
            };
        }
        // 阴影 caster 必须是带圆角的 material：ThemeShadow 按 caster 自身形状投影，
        // 放在矩形 Grid 上会投出方形阴影，在底部间隙里把两个圆角垫成直角外观。
        material.Shadow = reduced ? null : new ThemeShadow();
        material.Translation = reduced ? Vector3.Zero : new Vector3(0, 0, Preset == GlassPreset.Overlay ? 32 : 16);
        if (motionReduced || reduced) Illuminate(0);
    }
    void Illuminate(float opacity)
    {
        var visual = ElementCompositionPreview.GetElementVisual(highlight);
        visual.StopAnimation("Opacity");
        if (motionReduced || reduced) { visual.Opacity = 0; return; }
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, opacity); animation.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Opacity", animation);
    }
    public void SetScene(FrameworkElement backdrop, LoadedImageSurface? image)
    {
        scene = backdrop; source = image;
        if (lensBrush != null) lensBrush.Surface = source;
        UpdateLens();
    }
    public void UpdateLens()
    {
        if (!IsLoaded || reduced || source == null || scene == null || ActualWidth <= 0 || ActualHeight <= 0)
        { ReleaseLens(); return; }
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            if (lens == null)
            {
                lensBrush = compositor.CreateSurfaceBrush(source); lensBrush.Stretch = CompositionStretch.None;
                lens = compositor.CreateSpriteVisual(); lens.Brush = lensBrush; lens.Opacity = Preset == GlassPreset.Player ? .40f : .30f;
                ElementCompositionPreview.SetElementChildVisual(lensHost, lens);
            }
            float w = (float)ActualWidth, h = (float)ActualHeight;
            lens.Size = new(w, h);
            var position = TransformToVisual(scene).TransformPoint(new Point());
            // Ambient images use a shared 1920 x 1200 coordinate space and UniformToFill.
            float scale = (float)Math.Max(scene.ActualWidth / 1920, scene.ActualHeight / 1200);
            const float magnification = 1.035f;
            var origin = new Vector2((float)(scene.ActualWidth - 1920 * scale) / 2 - (float)position.X,
                (float)(scene.ActualHeight - 1200 * scale) / 2 - (float)position.Y);
            lensBrush!.TransformMatrix = Matrix3x2.CreateScale(scale * magnification) *
                Matrix3x2.CreateTranslation(origin - new Vector2(w, h) * ((magnification - 1) / 2));
            using var outer = CanvasGeometry.CreateRoundedRectangle(null, 0, 0, w, h, 26, 26);
            using var inner = CanvasGeometry.CreateRoundedRectangle(null, 3, 3, Math.Max(1, w - 6), Math.Max(1, h - 6), 23, 23);
            using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            lens.Clip = null; lensClip?.Dispose(); lensGeometry?.Dispose();
            lensGeometry = compositor.CreatePathGeometry(new CompositionPath(ring));
            lensClip = compositor.CreateGeometricClip(lensGeometry); lens.Clip = lensClip;
        }
        catch (Exception ex)
        {
            Services.AppPaths.Log("Glass fallback: " + ex.Message);
            reduced = true; ReleaseLens(); Refresh();
        }
    }
    void ReleaseLens()
    {
        ElementCompositionPreview.SetElementChildVisual(lensHost, null);
        lens?.Dispose(); lens = null; lensBrush?.Dispose(); lensBrush = null;
        lensClip?.Dispose(); lensClip = null; lensGeometry?.Dispose(); lensGeometry = null;
    }
}
