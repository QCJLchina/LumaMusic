using System.Diagnostics;
using System.Text.Json;
using LumaMusic.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LumaMusic;

// Compiled only with -p:LumaUiRegression=true and gated to a build/ test profile.
public sealed partial class MainWindow
{
    partial void StartUiRegression()
    {
        if (Environment.GetEnvironmentVariable("LUMA_UI_REGRESSION") != "1") return;
        string expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "build")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(AppPaths.Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UI regression requires a build/ profile and repository working directory.");
        _ = RunUiRegression();
    }

    async Task RunUiRegression()
    {
        var checks = new List<object>();
        void Check(string name, bool ok, object? detail = null)
        {
            checks.Add(new { name, passed = ok, detail });
            if (!ok) throw new InvalidOperationException(name + ": " + JsonSerializer.Serialize(detail));
        }
        async Task Settle(int milliseconds = 180) { await Task.Delay(milliseconds); Root.UpdateLayout(); }
        async Task Until(Func<bool> predicate)
        {
            for (int i = 0; i < 100 && !predicate(); i++) await Task.Delay(50);
            if (!predicate()) throw new TimeoutException("UI did not settle within 5 seconds");
        }
        bool Within(FrameworkElement child)
        {
            var point = child.TransformToVisual(Root).TransformPoint(new());
            return child.ActualWidth > 0 && child.ActualHeight > 0 && point.X >= -1 && point.Y >= -1
                && point.X + child.ActualWidth <= Root.ActualWidth + 1 && point.Y + child.ActualHeight <= Root.ActualHeight + 1;
        }
        try
        {
            await Until(() => ready); await Settle(800);
            Check("fixture loaded", tracks.Count == 36);
            Check("albums are default", showAlbums && AlbumGrid.Visibility == Visibility.Visible);
            foreach (int theme in new[] { 1, 2 })
            foreach (var size in new[] { (1000, 660), (1320, 860), (1920, 1080) })
            {
                prefs.Theme = theme; ApplyTheme();
                AppWindow.Resize(new(size.Item1, size.Item2)); await Settle(350);
                Library_Click(this, new()); await Settle();
                Check($"library bounds {theme} {size}", Within(SearchBox) && Within(AlbumGrid) && Within(PlayButton) && Within(SeekSlider),
                    new { Root.ActualWidth, Root.ActualHeight, dpi = Root.XamlRoot.RasterizationScale, seek = SeekSlider.ActualWidth });
                Check($"compact policy {theme} {size}", (Root.ActualWidth < 1100) == (CompactVolume.Visibility == Visibility.Visible));
                Now_Click(this, new()); await Settle();
                Check($"now bounds {theme} {size}", Within(LargeCover) && Within(LyricsScroll) && Within(SignalPath));
                Queue_Click(this, new()); await Settle();
                Check($"queue bounds {theme} {size}", Within(QueuePanel));
                Queue_Click(this, new());
            }
            AppWindow.Resize(new(1320, 860)); prefs.Theme = 1; ApplyTheme(); await Settle();
            Library_Click(this, new()); SongsTab_Click(this, new());
            Check("songs view", TrackList.Visibility == Visibility.Visible && visible.Count == 36);
            ArtistsTab_Click(this, new());
            Check("artist aggregation", ArtistList.ItemsSource is List<ArtistSummary> artists && artists.Count == 6 && artists.Sum(a => a.Songs) == 36);
            artistFilter = "林间回声"; showArtists = false; showAlbums = false; Filter();
            Check("artist detail", visible.Count == 6 && BrowseBack.Visibility == Visibility.Visible);
            BrowseBack_Click(this, new()); Check("artist return", showArtists && artistFilter == null);
            Favorites_Click(this, new()); Check("favorites", visible.Count == 12 && visible.All(t => t.Favorite));
            playlistFilter = 1; favoritesOnly = false; Filter(); Check("playlist", visible.Count == 1);
            Library_Click(this, new()); SearchBox.Text = "月光海岸"; Filter();
            Check("search", visible.Count == 3 && visible.All(t => t.Album == "月光海岸"));
            SearchBox.Text = "no-such-track"; Filter(); Check("empty search", visible.Count == 0 && EmptyState.Visibility == Visibility.Visible);
            SearchBox.Text = ""; Filter();

            var originalTracks = tracks;
            tracks = Enumerable.Range(0, 10000).Select(i => new Track { Id = "large-" + i, Album = "Album " + (i / 10), Artist = "Artist " + (i % 50), Title = "Track " + i }).ToList();
            var watch = Stopwatch.StartNew(); Filter(); await Settle();
            Check("large library", visible.Count == 10000, new { milliseconds = watch.ElapsedMilliseconds });
            tracks = []; Filter(); Check("empty library", EmptyState.Visibility == Visibility.Visible);
            tracks = originalTracks; Filter();

            var covers = tracks.Where(t => t.Cover.Length > 0).Select(t => t.Cover).Distinct().ToArray();
            for (int i = 0; i < 20; i++) Ambient.SetSource(covers[i % covers.Length]);
            Ambient.SetSource(null); await Settle(1400);
            Check("latest cover wins after clear", Ambient.CurrentBackgroundPath == null);
            Ambient.SetSource(covers[0]); await Until(() => Ambient.CurrentBackgroundPath != null); await Settle(700);
            Check("cover background rendered", File.Exists(Ambient.CurrentBackgroundPath));
            foreach (var surface in GlassSurfaces) surface.Configure(true, true, false);
            Check("reduced transparency fallback", GlassSurfaces.All(s => s.UsesSolidMaterial && !s.HasLens));
            prefs.ReducedMotion = true; RefreshAppearance(); Check("reduced motion policy", Ambient.Reduced && Controls.InteractionMotion.Reduced);
            prefs.ReducedMotion = false; RefreshAppearance();
            SetVisualActivity(false); Check("hidden window pauses background", !Ambient.Active);
            SetVisualActivity(true); Check("restored window resumes policy", Ambient.Active);

            // A real native engine and digital-silence fixture, never synthesized success.
            if (audio == null || Device == null) throw new InvalidOperationException("Audio device unavailable");
            prefs.Online = false; prefs.Volume = 0; prefs.Profile.Backend = 0;
            var track = originalTracks.First(t => t.Id == "ui-0-0");
            await Play(track); await Settle(1500);
            Check("real silent playback", lastState.Playing && lastState.Position > 0 && !lastState.Failed, lastState);
            long underruns = lastState.Underruns;
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
            var frames = new List<double>(); var last = Stopwatch.GetTimestamp();
            void Frame(object? sender, object args) { var now = Stopwatch.GetTimestamp(); frames.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds); last = now; }
            CompositionTarget.Rendering += Frame;
            for (int i = 0; i < 12; i++) { Queue_Click(this, new()); await Settle(250); }
            CompositionTarget.Rendering -= Frame;
            process.Refresh(); var ordered = frames.Order().ToArray();
            Check("playback survives UI transitions", !lastState.Failed && lastState.Playing && lastState.Underruns == underruns,
                new { cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, workingSetMB = process.WorkingSet64 / 1048576,
                    frames = frames.Count, p95UiCallbackMs = ordered.Length > 0 ? ordered[(int)((ordered.Length - 1) * .95)] : 0,
                    underrunsBefore = underruns, underrunsAfter = lastState.Underruns });
            await SeekTo(20); await Settle(500); Check("seek", lastState.Position >= 19 && !lastState.Failed);
            audio.Pause(true); await Settle(300); Check("pause", !lastState.Playing);
            audio.Pause(false); await Settle(300); Check("resume", lastState.Playing);
            await audio.Stop(); await Settle();
            Now_Click(this, new()); await Settle();
            Check("lyrics rendered", lyricButtons.Count > 0);
            prefs.Theme = 2; ApplyTheme(); await Settle();
            Check("active lyric stays opaque after theme change", lyricIndex >= 0 && lyricButtons[lyricIndex].Opacity == 1);
            prefs.Theme = 1; ApplyTheme();
            prefs.ReducedTransparency = true; AppPaths.Save(prefs);
            Check("appearance preference readback", AppPaths.Load().ReducedTransparency);
            prefs.ReducedTransparency = false; AppPaths.Save(prefs); RefreshAppearance();

            // Click behavior: double-click mode routes playback through DoubleTapped only.
            Library_Click(this, new()); SongsTab_Click(this, new()); await Settle();
            Check("double-click preference default off", !prefs.DoubleClickPlay);
            var tapTarget = visible.First(t => t.Id == "ui-3-0");
            TrackList.ScrollIntoView(tapTarget); await Settle();
            var tapContainer = TrackList.ContainerFromItem(tapTarget) as ListViewItem;
            var tapRoot = (FrameworkElement?)tapContainer?.ContentTemplateRoot ?? tapContainer;
            Check("double-click handler resolves item", tapContainer != null && tapRoot != null && ItemFromTapped(tapRoot) == tapTarget);
            prefs.DoubleClickPlay = true;
            Check("double-click mode ignores ItemClick", !queue.Any(t => t.Id == "ui-3-0"));
            await PlayFromTap(tapRoot, true); await Settle(200);
            Check("double-click handler plays", current?.Id == "ui-3-0" && queue.Any(t => t.Id == "ui-3-0"));
            prefs.DoubleClickPlay = false; AppPaths.Save(prefs);
            Check("click preference readback", !AppPaths.Load().DoubleClickPlay);
            await audio.Stop();

            var favoriteBefore = current!.Favorite;
            FavoriteCurrent_Click(this, new());
            Check("favorite persisted", library.Load().Single(t => t.Id == current.Id).Favorite != favoriteBefore);
            FavoriteCurrent_Click(this, new());
            QueueList.SelectedIndex = 1; var moved = queue[1]; QueueUp_Click(this, new());
            Check("queue reorder", queue[0] == moved);
            QueueList.SelectedIndex = 0; QueueRemove_Click(this, new()); Check("queue removal", !queue.Contains(moved));
            VolumeSlider.Value = 25; Check("volume change", Math.Abs(prefs.Volume - .25f) < .001f); VolumeSlider.Value = 0;
            library.CreatePlaylist("UI regression playlist"); RefreshPlaylists();
            var created = library.Playlists().Single(p => p.Name == "UI regression playlist");
            Check("playlist creation reaches navigation", PlaylistNav.Children.OfType<Button>().Any(b => b.Tag is Playlist p && p.Id == created.Id));
            library.DeletePlaylist(created.Id); RefreshPlaylists();
            await Import([track.Path]); Check("real WAV import", tracks.Count == 37 && tracks.Any(t => t.Title == "silence"));

            Library_Click(this, new()); await Settle();
        }
        catch (Exception ex) { checks.Add(new { name = "runner", passed = false, detail = ex.ToString() }); }
        finally
        {
            File.WriteAllText(Path.Combine(AppPaths.Root, "ui-regression.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (Environment.GetEnvironmentVariable("LUMA_UI_REGRESSION_EXIT") == "1") { reallyClosing = true; Close(); }
        }
    }
}
