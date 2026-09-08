using LumaMusic.Controls;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.System.Power;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LumaMusic;

public sealed partial class MainWindow
{
    readonly UISettings systemUi = new();
    readonly AccessibilitySettings accessibility = new();
    readonly DispatcherTimer appearanceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    LoadedImageSurface? glassBackground;
    CompositionCapabilities? capabilities;
    bool appearanceReady, compactLayout, activeVisuals = true, reduceMotion;
    int effectiveVisualQuality=2;
    bool ReduceMotion => reduceMotion || prefs.ReducedMotion;
    partial void StartUiRegression();
    int selectedNavigation = -1;
    (bool contrast, bool effects, bool animations, bool saving) lastSystemAppearance;
    GlassSurface[] GlassSurfaces => [SidebarGlass, PlayerGlass, QueuePanel];

    void InitializeAppearance()
    {
        appearanceReady = true;
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        Root.SizeChanged += (_, _) => UpdateResponsiveLayout();
        Root.Loaded += (_, _) =>
        {
            try { capabilities = new CompositionCapabilities(); capabilities.Changed += CapabilitiesChanged; } catch { }
            RefreshAppearance(); UpdateResponsiveLayout(); AttachInteractions(Root); UpdateNavigation();
            appearanceTimer.Start();
            StartUiRegression();
        };
        Ambient.BackgroundChanged += BackgroundChanged;
        systemUi.AdvancedEffectsEnabledChanged += SystemAppearanceChanged;
        systemUi.AnimationsEnabledChanged += SystemAppearanceChanged;
        // AccessibilitySettings.HighContrastChanged is not available on every unpackaged host.
        // Poll the system policy as well, without restarting unchanged composition animations.
        lastSystemAppearance = ReadSystemAppearance();
        appearanceTimer.Tick += (_, _) =>
        {
            bool active = AppWindow.IsVisible && !(AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized);
            if (activeVisuals != active) SetVisualActivity(active);
            var policy = ReadSystemAppearance();
            if (policy != lastSystemAppearance) { lastSystemAppearance = policy; RefreshAppearance(); }
        };
    }
    void SystemAppearanceChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshAppearance);
    (bool contrast, bool effects, bool animations, bool saving) ReadSystemAppearance()
        => (accessibility.HighContrast, systemUi.AdvancedEffectsEnabled, systemUi.AnimationsEnabled, PowerManager.EnergySaverStatus == EnergySaverStatus.On);
    void CapabilitiesChanged(CompositionCapabilities sender, object args) => DispatcherQueue.TryEnqueue(RefreshAppearance);
    void RefreshAppearance()
    {
        if (!appearanceReady || closing) return;
        bool highContrast = accessibility.HighContrast;
        bool effects = systemUi.AdvancedEffectsEnabled;
        bool energySaver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        bool capable = capabilities == null || (capabilities.AreEffectsSupported() && capabilities.AreEffectsFast());
        bool solid = prefs.ReducedTransparency || highContrast || !effects || energySaver || !capable;
        // WinUI exposes effect capability but not a stable dedicated-memory value on every
        // unpackaged host. Automatic therefore starts conservatively (the 780M class of
        // shared-memory adapters uses the lightweight path); users can opt into Complete.
        effectiveVisualQuality = prefs.VisualQuality switch { 1 => 1, 2 => 2, _ => 1 };
        if (effectiveVisualQuality==2 && !capable) effectiveVisualQuality=1;
        reduceMotion = prefs.ReducedMotion || !systemUi.AnimationsEnabled || energySaver || highContrast;
        InteractionMotion.Reduced = ReduceMotion || !activeVisuals;
        Ambient.Reduced = ReduceMotion;
        Ambient.Aurora = prefs.Aurora && !solid;
        Ambient.Quality = effectiveVisualQuality;
        Ambient.ThemeMode = LightTheme ? "light" : "dark";
        Ambient.SetActive(activeVisuals);
        Ambient.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        foreach (var surface in GlassSurfaces) surface.Configure(solid || !activeVisuals, ReduceMotion || !activeVisuals, highContrast, effectiveVisualQuality==1 && !solid);
        if (highContrast)
        {
            Root.Background = SystemBrush("SystemControlBackgroundAltHighBrush");
            Root.Resources["TextPrimary"] = SystemBrush("SystemControlForegroundBaseHighBrush");
            Root.Resources["TextSecondary"] = SystemBrush("SystemControlForegroundBaseHighBrush");
            Root.Resources["ButtonFill"] = SystemBrush("SystemControlBackgroundAltHighBrush");
            Root.Resources["ButtonFillStrong"] = SystemBrush("SystemControlHighlightAccentBrush");
            Root.Resources["StrokeWeak"] = SystemBrush("SystemControlForegroundBaseHighBrush");
            Root.Resources["Accent"] = SystemBrush("SystemControlForegroundBaseHighBrush");
            Root.Resources["PlayIconColor"] = SystemBrush("SystemControlBackgroundAltHighBrush");
        }
        else
        {
            foreach (var key in new[] { "TextPrimary", "TextSecondary", "ButtonFill", "ButtonFillStrong", "StrokeWeak", "Accent", "PlayIconColor" }) Root.Resources.Remove(key);
            Root.Background = (Brush)AccentDict()["WindowFallback"];
        }
        LargeCover.Shadow = solid ? null : new ThemeShadow();
        RefreshLyricColors(); UpdateBrowseSelection();
    }
    static Brush SystemBrush(string key) => (Brush)Application.Current.Resources[key];
    void SetVisualActivity(bool active)
    {
        activeVisuals = active; RefreshAppearance();
    }
    void BackgroundChanged(string? file)
    {
        var previous = glassBackground;
        glassBackground = file == null ? null : LoadedImageSurface.StartLoadFromUri(new Uri(file));
        foreach (var surface in GlassSurfaces) surface.SetScene(Ambient, glassBackground);
        previous?.Dispose();
    }
    void EnsureWindowMinimum()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Ceiling(760 * scale), height = (int)Math.Ceiling(560 * scale);
        if (AppWindow.Size.Width < width || AppWindow.Size.Height < height)
            AppWindow.Resize(new(Math.Max(width, AppWindow.Size.Width), Math.Max(height, AppWindow.Size.Height)));
    }
    void UpdateResponsiveLayout()
    {
        compactLayout = Root.ActualWidth < 1100;
        SidebarColumn.Width = new(nowVisible ? 0 : compactLayout ? 76 : 208);
        Workspace.ColumnSpacing = nowVisible ? 0 : compactLayout ? 16 : 28;
        foreach (var label in new FrameworkElement[] { LibraryNavLabel, NowNavLabel, FavoritesNavLabel, ImportNavLabel, SettingsNavLabel, PlaylistHeading, LibraryCount })
            label.Visibility = compactLayout ? Visibility.Collapsed : Visibility.Visible;
        PlayerInfoColumn.Width = new(compactLayout ? 180 : 250);
        PlayerToolsColumn.Width = new(compactLayout ? 120 : 190);
        PlayerLayout.ColumnSpacing = compactLayout ? 12 : 20;
        VolumeSlider.Visibility = compactLayout ? Visibility.Collapsed : Visibility.Visible;
        CompactVolume.Visibility = compactLayout ? Visibility.Visible : Visibility.Collapsed;
        double available = Math.Max(160, (Root.ActualWidth - (nowVisible ? 120 : compactLayout ? 170 : 330)) / 2 - 70);
        LargeCover.Width = LargeCover.Height = Math.Clamp(Math.Min(available, Root.ActualHeight - 300), 160, 390);
        CoverReflection.Width = LargeCover.Width; CoverReflection.Height = Math.Clamp(LargeCover.Height * .28, 55, 105);
        QueuePanel.Width = Math.Min(340, Math.Max(260, Root.ActualWidth - 160));
        foreach (var button in PlaylistNav.Children.OfType<Button>())
            if (button.Tag is Playlist playlist) ConfigurePlaylistButton(button, playlist);
        foreach (var surface in GlassSurfaces) surface.UpdateLens();
    }
    void ConfigurePlaylistButton(Button button, Playlist playlist)
    {
        button.Content = compactLayout ? "♫" : "♫  " + playlist.Name;
        ToolTipService.SetToolTip(button, playlist.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, playlist.Name);
        InteractionMotion.Attach(button);
        button.Background = playlistFilter == playlist.Id && !nowVisible
            ? SelectionBrush() : new SolidColorBrush(Colors.Transparent);
    }
    void UpdateNavigation()
    {
        int index = nowVisible ? 1 : favoritesOnly ? 2 : playlistFilter != null ? -1 : 0;
        NavSelection.Visibility = index < 0 ? Visibility.Collapsed : Visibility.Visible;
        if (index >= 0 && index != selectedNavigation)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(NavSelection, true);
            var visual = ElementCompositionPreview.GetElementVisual(NavSelection);
            var target = new Vector3(0, index * 52, 0);
            visual.StopAnimation("Translation");
            if (ReduceMotion || selectedNavigation < 0) visual.Properties.InsertVector3("Translation", target);
            else
            {
                var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
                animation.InsertKeyFrame(1, target); animation.Duration = TimeSpan.FromMilliseconds(260);
                visual.StartAnimation("Translation", animation);
            }
        }
        selectedNavigation = index;
        foreach (var button in PlaylistNav.Children.OfType<Button>())
            if (button.Tag is Playlist playlist) ConfigurePlaylistButton(button, playlist);
    }
    void UpdateBrowseSelection()
    {
        if (!appearanceReady) return;
        foreach (var (button, selected) in new[] { (AlbumsTab, showAlbums), (ArtistsTab, showArtists), (SongsTab, !showAlbums && !showArtists) })
        {
            button.Background = selected ? SelectionBrush() : new SolidColorBrush(Colors.Transparent);
            button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
    Brush SelectionBrush() => accessibility.HighContrast ? SystemBrush("SystemControlHighlightAccentBrush") : (Brush)AccentDict()["ButtonFillStrong"];
    static void AttachInteractions(DependencyObject root)
    {
        if (root is Button button) InteractionMotion.Attach(button);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) AttachInteractions(VisualTreeHelper.GetChild(root, i));
    }
    void DisposeAppearance()
    {
        appearanceTimer.Stop();
        systemUi.AdvancedEffectsEnabledChanged -= SystemAppearanceChanged;
        systemUi.AnimationsEnabledChanged -= SystemAppearanceChanged;
        Ambient.SetActive(false);
        Ambient.Shutdown();
        Ambient.BackgroundChanged -= BackgroundChanged;
        foreach (var surface in GlassSurfaces) surface.SetScene(Ambient, null);
        glassBackground?.Dispose();
        if (capabilities != null) capabilities.Changed -= CapabilitiesChanged;
    }
}
