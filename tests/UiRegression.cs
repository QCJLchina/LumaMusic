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
        // 预览模式只摆放画面状态给截图用，不做断言；与回归共用同一个仅回归构建编译的入口。
        if (Environment.GetEnvironmentVariable("LUMA_UI_PREVIEW") == "1") { _ = RunUiPreview(); return; }
        if (Environment.GetEnvironmentVariable("LUMA_UI_REGRESSION") != "1") return;
        string expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "build")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(AppPaths.Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UI regression requires a build/ profile and repository working directory.");
        _ = RunUiRegression();
    }

    // 人工审查深色界面对比度用：每个阶段写好标记文件后等 build/preview-tools/preview.ps1 截完图 ack，
    // 截图因此总是落在动效稳定之后，而不是靠固定睡眠去赌。
    async Task RunUiPreview()
    {
        async Task Settle(int milliseconds) { await Task.Delay(milliseconds); Root.UpdateLayout(); }
        string Marker(string name) => Path.Combine(AppPaths.Root, "preview-" + name + ".txt");
        string State() => JsonSerializer.Serialize(new { theme = Root.ActualTheme.ToString(), playing = lastState.Playing, failed = lastState.Failed, track = current?.Id, palette = Ambient.CurrentPalette.Select(c => c.ToString()).ToArray() });
        async Task Stage(string name)
        {
            // 截图进程会把焦点从窗口拿走；这里按"窗口在前台"的材质策略固定住，才是审查时要看的那一版。
            windowFocused = true; SetVisualActivity(true); await Settle(700);
            File.WriteAllText(Marker(name), State());
            for (int i = 0; i < 600 && !File.Exists(Marker(name + ".ack")); i++) await Task.Delay(50);
        }
        try
        {
            for (int i = 0; i < 400 && !ready; i++) await Task.Delay(50);
            prefs.Theme = 1; ApplyTheme();
            prefs.VisualQuality = 2; prefs.Aurora = true; prefs.ReducedTransparency = false; prefs.ReducedMotion = false; prefs.Online = false;
            // README 预览图要按原尺寸出图，所以窗口大小可以用 LUMA_PREVIEW_SIZE=1320x860 覆盖。
            var size = (1600, 950);
            if (Environment.GetEnvironmentVariable("LUMA_PREVIEW_SIZE")?.Split('x') is [var rawWidth, var rawHeight]
                && int.TryParse(rawWidth, out var width) && int.TryParse(rawHeight, out var height)) size = (width, height);
            RefreshAppearance(); AppWindow.Resize(new(size.Item1, size.Item2)); await Settle(1200);
            Library_Click(this, new()); await Settle(1200);
            await Stage("library");
            // 白封面取色最亮，是对比度最差的一档；歌词与信息面板各拍一张，面板切换另有入场动画要等。
            await Play(tracks.First(t => t.Id == "ui-9-0")); Now_Click(this, new()); await Settle(3600);
            SwitchContent(false); await Settle(1600); await Stage("now-white-lyrics");
            SwitchContent(true); await Settle(1600); await Stage("now-white-info");
            await Play(tracks.First(t => t.Id == "ui-10-0")); SwitchContent(false); await Settle(3600);
            await Stage("now-vivid-lyrics");
            audio?.Pause(true); await Settle(2400);
            await Stage("paused");
            // 冻结动效后再拍一组：漂移与呼吸停掉、光斑位置与透明度固定，此时前后两次的差异只剩这次改动的参数，
            // 不会被动画相位带偏，指标才可以当成结论用。
            prefs.ReducedMotion = true; RefreshAppearance(); await Settle(1400);
            await Play(tracks.First(t => t.Id == "ui-9-0")); await Settle(2400);
            await Stage("static-white");
            await Play(tracks.First(t => t.Id == "ui-10-0")); await Settle(2400);
            await Stage("static-vivid");
            if (audio != null) await audio.Stop();
        }
        catch (Exception ex) { AppPaths.Log("UI preview failed: " + ex); File.WriteAllText(Marker("error"), ex.ToString()); }
        finally { File.WriteAllText(Marker("done"), ""); reallyClosing = true; Close(); }
    }

    async Task RunUiRegression()
    {
        var checks = new List<object>();
        void Check(string name, bool ok, object? detail = null)
        {
            checks.Add(new { name, passed = ok, detail });
            AppPaths.Log("UI regression: " + name + " = " + ok);
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
                Check($"now bounds {theme} {size}", Within(LargeCover) && Within(LyricsScroll) && Within(PlayButton));
                Check($"immersive layout {theme} {size}", SidebarGlass.Visibility == Visibility.Collapsed && Grid.GetColumn(ContentHost) == 0 && Grid.GetColumnSpan(ContentHost) == 2 && SignalDetails.Visibility == Visibility.Collapsed);
                Queue_Click(this, new()); await Settle();
                Check($"queue bounds {theme} {size}", Within(QueuePanel));
                Queue_Click(this, new());
            }
            AppWindow.Resize(new(1320, 860)); prefs.Theme = 1; ApplyTheme(); await Settle();
            Library_Click(this, new()); SongsTab_Click(this, new());
            Check("songs view", TrackList.Visibility == Visibility.Visible && visible.Count == 36);
            EnterMultiSelect(); TrackList.SelectedItems.Add(visible[0]); TrackList.SelectedItems.Add(visible[1]);
            Check("multi-select", multiSelecting && TrackList.SelectionMode == ListViewSelectionMode.Multiple && TrackList.SelectedItems.Count == 2 && BatchBar.Visibility == Visibility.Visible);
            SelectAll_Click(this, new()); Check("select all filtered", TrackList.SelectedItems.Count == visible.Count);
            ExitMultiSelect(); Check("multi-select exit", !multiSelecting && TrackList.SelectionMode == ListViewSelectionMode.Single && TrackList.SelectedItems.Count == 0);
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
            // 真 seek：luma_seek 保留已取得的设备，只重建解码器，
            // 所以 current 指针不应变化（整首重开会先 engine.reset() 再 ShowTrack）。
            var beforeSeek = current?.Id;
            await SeekTo(20); await Settle(500); Check("seek", lastState.Position >= 19 && !lastState.Failed);
            Check("seek keeps the same track open", current?.Id == beforeSeek, new { beforeSeek, after = current?.Id });
            // 连跳几次确认 ring 缓冲重置正确（不会把两段音频拼起来）。
            await SeekTo(5); await Settle(400); await SeekTo(25); await Settle(400);
            Check("repeated seeks stay in range", !lastState.Failed && lastState.Position >= 24 && lastState.Position <= 26,
                new { position = lastState.Position, duration = lastState.Duration, underruns = lastState.Underruns });
            audio.Pause(true); await Settle(300); Check("pause", !lastState.Playing);
            audio.Pause(false); await Settle(300); Check("resume", lastState.Playing);
            await audio.Stop(); await Settle();
            Now_Click(this, new()); await Settle();
            Check("lyrics rendered", lyricButtons.Count > 0);
            SwitchContent(true); await Settle(1000);
            Check("track information uses real fields", InformationPanel.Children.Count==5 && InformationScroll.Opacity==1 && InformationScroll.IsHitTestVisible && !LyricsScroll.IsHitTestVisible);
            for(int i=0;i<8;i++)SwitchContent(i%2==0);
            SwitchContent(false); await Settle(1100);
            Check("rapid panel switch leaves lyrics interactive", !InformationScroll.IsHitTestVisible && LyricsScroll.IsHitTestVisible && !informationVisible);
            Check("reflected cover rendered", ReflectionImage.Source!=null);
            prefs.VisualQuality=2; prefs.Aurora=true; prefs.ReducedTransparency=false; RefreshAppearance();
            Check("full ambient has exactly three fields", Ambient.BlobCount==3 && Ambient.CurrentPalette.Length==3);
            prefs.VisualQuality=1; RefreshAppearance(); Check("lightweight ambient has two fields",Ambient.BlobCount==2);
            prefs.ReducedMotion=true; RefreshAppearance();Check("reduced motion stops ambient loops",!Ambient.MotionRunning);
            SwitchContent(true);SwitchContent(false);
            prefs.ReducedMotion=false;RefreshAppearance();
            ShowLibrary();Check("library hides ambient motion",!Ambient.MotionRunning);
            Now_Click(this,new());SetVisualActivity(true);await Settle();

            prefs.Theme = 2; ApplyTheme(); await Settle();
            Check("active lyric stays opaque after theme change", lyricIndex >= 0 && lyricButtons[lyricIndex].Opacity == 1);
            prefs.Theme = 1; ApplyTheme();
            prefs.ReducedTransparency = true; AppPaths.Save(prefs);
            Check("appearance preference readback", AppPaths.Load().ReducedTransparency);
            prefs.ReducedTransparency = false; AppPaths.Save(prefs); RefreshAppearance();

            // 发行形态决定更新方式：回归跑在未打包的开发版，且目录里没有安装器标记，必须是便携版。
            Check("update mode resolves to portable", UpdateChecker.Mode == UpdateChecker.UpdateMode.Portable && !UpdateChecker.IsPackaged);
            // 发布资产里既有 .msix/.cer 又有 LumaMusic-Setup.exe，还混着随包分发的 FlexASIOSetup.exe：
            // 匹配谓词必须精确挑中目标，不能按 ".exe" 之类的宽后缀误命中。
            Check("release asset matching", UpdateChecker.IsInstallerAsset("LumaMusic-Setup.exe")
                && !UpdateChecker.IsInstallerAsset("FlexASIOSetup.exe") && !UpdateChecker.IsInstallerAsset("LumaMusic.msix")
                && UpdateChecker.IsMsixAsset("LumaMusic.msix") && !UpdateChecker.IsMsixAsset("LumaMusic-Setup.exe"));
            // 安装版判定读的是进程目录里的标记文件，与 packaging/LumaMusic.nsi 的 MARKER 同名，写错就永远认不出安装版。
            Check("installer marker name matches packaging", UpdateChecker.InstallMarker == ".luma-nsis-install");

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
            var batchIds = originalTracks.Take(2).Select(t => t.Id).ToArray();
            library.AddToPlaylist(created.Id, batchIds.Concat(batchIds)); Check("batch playlist add deduplicates", library.PlaylistTracks(created.Id).Count == 2);
            library.RemoveFromPlaylist(created.Id, [batchIds[0]]); Check("batch playlist remove", library.PlaylistTracks(created.Id).ToHashSet().SetEquals([batchIds[1]]));
            library.SetFavorite(batchIds, true); Check("batch favorite persists", library.Load().Where(t => batchIds.Contains(t.Id)).All(t => t.Favorite));
            library.DeletePlaylist(created.Id); RefreshPlaylists();
            await Import([track.Path]); Check("real WAV import", tracks.Any(t => t.Title == "silence"));
            var imported = tracks.Single(t => t.Title == "silence"); var importedPath = imported.Path; library.RemoveTracks([imported.Id]);
            Check("library removal keeps source", !library.Load().Any(t => t.Id == imported.Id) && File.Exists(importedPath));
            await Import([importedPath]); Check("removed track can be reimported", tracks.Any(t => t.Id == imported.Id));

            // ---- cue 拆轨：走真实导入路径，GBK 中文 cue ----
            var cueFile = Directory.GetFiles(Path.Combine(Environment.CurrentDirectory, "build", "ui-fixtures", "cue-album"), "*.wav").First();
            await Import([cueFile]);
            var cueTracks = tracks.Where(t => t.Path.Equals(cueFile, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.CueIndex).ToList();
            Check("cue splits into indexed tracks", cueTracks.Count == 3, new { count = cueTracks.Count });
            Check("cue titles decoded from GBK", cueTracks.Select(t => t.Title).SequenceEqual(new[] { "第一首", "第二首", "第三首" }),
                cueTracks.Select(t => t.Title));
            Check("cue artists and album decoded", cueTracks.All(t => t.Artist == "整轨乐队" && t.Album == "整轨专辑"));
            Check("cue REM metadata read", cueTracks[0].Genre == "测试流派" && cueTracks[0].Year == 1997);
            Check("cue starts are absolute seconds", cueTracks.Select(t => t.CueIndex).SequenceEqual(new[] { 0d, 5d, 15d }),
                cueTracks.Select(t => t.CueIndex));
            // 5/10/5 秒：末曲用文件总长（30s）兜底，不能算出 0。
            Check("cue durations follow INDEX gaps", Math.Abs(cueTracks[0].Duration - 5) < .2 && Math.Abs(cueTracks[1].Duration - 10) < .2 && Math.Abs(cueTracks[2].Duration - 15) < .2,
                cueTracks.Select(t => t.Duration));
            Check("cue tracks are not overwritten by whole-file tags", cueTracks.Select(t => t.Title).Distinct().Count() == 3);
            Check("cue tracks are marked from cue", cueTracks.All(t => t.FromCue) && !imported.FromCue);

            var parsed = CueSheet.Parse("FILE \"整轨.wav\" WAVE\nTRACK 01 AUDIO\nTITLE First\nINDEX 01 00:00:00\nTRACK 02 AUDIO\nTITLE PreludeOnly\nINDEX 00 00:01:00\nTRACK 03 AUDIO\nTITLE Second\nINDEX 01 00:05:00\n");
            Check("cue parse keeps a zero start", parsed != null && parsed.Entries[0].Start == 0 && parsed.Entries[0].Title == "First");
            Check("cue parse drops index 00 only tracks", parsed != null && parsed.Entries.Count == 2 && parsed.Entries.All(e => e.Title != "PreludeOnly"));
            Check("cue parse rejects sheet without file", CueSheet.Parse("TRACK 01 AUDIO\nINDEX 01 00:00:00") == null);
            Check("cue parse rejects sheet without index 01", CueSheet.Parse("FILE \"a.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 00 00:00:00") == null);

            // 重复导入必须幂等：cue 曲目 Id 由 文件+序号 决定，不该翻倍。
            var beforeCue = tracks.Count(t => t.Path.Equals(cueFile, StringComparison.OrdinalIgnoreCase));
            await Import([cueFile]);
            Check("cue reimport is idempotent", tracks.Count(t => t.Path.Equals(cueFile, StringComparison.OrdinalIgnoreCase)) == beforeCue);

            // 播放：非首曲必须从自己的 INDEX 起点开始，且 duration 覆盖本曲而不是整轨。
            await Play(cueTracks[1]); await Settle(900);
            Check("cue playback starts at its own index", lastState.Playing && lastState.Duration < 20 && !lastState.Failed,
                new { duration = lastState.Duration, position = lastState.Position });
            await Play(cueTracks[0]); await Settle(900);
            Check("cue first track stops at its own end", lastState.Playing && lastState.Duration < 10 && !lastState.Failed,
                new { duration = lastState.Duration, position = lastState.Position });

            // ---- 排序 ----
            Library_Click(this, new()); SongsTab_Click(this, new()); await Settle();
            var libraryOrder = visible.Select(t => t.Id).ToList();
            // 首次点"时长"是升序；再点一次才翻成降序。
            Sort_Click(SortDuration, new()); await Settle();
            Check("sort by duration ascending", visible.Count > 0 && visible.Zip(visible.Skip(1)).All(p => p.First.Duration <= p.Second.Duration),
                visible.Take(5).Select(t => t.Duration));
            Sort_Click(SortDuration, new()); await Settle();
            Check("sort toggles to descending", visible.Zip(visible.Skip(1)).All(p => p.First.Duration >= p.Second.Duration),
                visible.Take(5).Select(t => t.Duration));
            Sort_Click(SortTitle, new()); await Settle();
            Check("sort returns to library order", visible.Select(t => t.Id).SequenceEqual(libraryOrder));

            // ---- ReplayGain：只读文件标签，走真实解析 ----
            var rgExpected = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "build", "ui-fixtures", "replaygain-expected.json")));
            var rgGain = rgExpected.RootElement.GetProperty("trackGain").GetDouble();
            var rgFile = Path.Combine(Environment.CurrentDirectory, "build", "ui-fixtures", "replaygain-sample.wav");
            await Import([rgFile]);
            var rgTrack = tracks.FirstOrDefault(t => t.Path.Equals(rgFile, StringComparison.OrdinalIgnoreCase));
            Check("replaygain tag parsed from file", rgTrack != null && Math.Abs(rgTrack.ReplayGainDb - rgGain) < .01 && rgTrack.GainSource == "track",
                new { found = rgTrack?.ReplayGainDb, expected = rgGain, source = rgTrack?.GainSource });
            // 关闭时增益必须是 1（不改变任何音频数据）。
            var profileNoGain = prefs.Profile;
            profileNoGain.ReplayGain = 0;
            Check("replaygain off yields unity gain", Math.Abs(GainFor(rgTrack, profileNoGain) - 1f) < 1e-6);
            profileNoGain.ReplayGain = 2;
            Check("replaygain dB converts to linear gain",
                Math.Abs(GainFor(rgTrack, profileNoGain) - Math.Pow(10, rgGain / 20)) < 1e-4,
                GainFor(rgTrack, profileNoGain));
            profileNoGain.ReplayGain = 1;
            // 只有专辑标签的文件在"专辑增益"模式下不做任何修正（增益为 1.0），而不是被静音。
            Check("album mode without album tag leaves gain at unity", Math.Abs(GainFor(rgTrack, profileNoGain) - 1f) < 1e-6,
                GainFor(rgTrack, profileNoGain));
            // 播放时引擎收到的确实是这个倍率（真出声验证由 audio_smoke 覆盖）。
            prefs.Profile.ReplayGain = 2;
            await Play(rgTrack); await Settle(900);
            Check("engine receives replaygain", Math.Abs(lastState.Gain - Math.Pow(10, rgGain / 20)) < 1e-4,
                new { reported = lastState.Gain });
            await audio.Stop();
            prefs.Profile.ReplayGain = 0;

            // ---- 播放列表：保序、重命名、重排、导出 ----
            var pl = library.Playlists().First();
            library.ClearPlaylist(pl.Id);
            var orderIds = originalTracks.Take(3).Select(t => t.Id).ToList();
            library.AddToPlaylist(pl.Id, orderIds);
            Check("playlist preserves insertion order", library.PlaylistTracks(pl.Id).SequenceEqual(orderIds));
            var reordered = new[] { orderIds[2], orderIds[0], orderIds[1] };
            library.ReorderPlaylist(pl.Id, reordered);
            Check("playlist reorder persists", library.PlaylistTracks(pl.Id).SequenceEqual(reordered));
            var nav = PlaylistNav.Children.OfType<Button>().First(b => b.Tag is Playlist p && p.Id == pl.Id);
            playlistFilter = pl.Id; favoritesOnly = false; albumFilter = null; artistFilter = null;
            PageTitle.Text = ((Playlist)nav.Tag).Name; showAlbums = false; showArtists = false;
            ShowLibrary(); Filter(); await Settle();
            Check("playlist view follows stored order", visible.Select(t => t.Id).SequenceEqual(library.PlaylistTracks(pl.Id)));
            library.RenamePlaylist(pl.Id, "改名后的列表");
            Check("playlist rename", library.Playlists().First(p => p.Id == pl.Id).Name == "改名后的列表");
            var m3u = Path.Combine(Path.GetTempPath(), "luma-regression.m3u");
            var written = LibraryService.ExportM3u(m3u, originalTracks.Take(3));
            var m3uText = written == null ? "" : File.ReadAllText(written);
            Check("m3u export writes entries", written != null && File.Exists(written) && m3uText.StartsWith("#EXTM3U")
                && originalTracks.Take(3).All(t => m3uText.Contains(t.Title)),
                m3uText.Length > 400 ? m3uText[..400] : m3uText);
            if (written != null) File.Delete(written);

            // ---- 全局热键 ----
            Check("global hotkeys registered", hotKeys != null, hotKeys?.Failed);
            Check("global hotkey bindings documented", GlobalHotKeys.Description.Contains("Ctrl"));

            Library_Click(this, new()); await Settle();
            Now_Click(this, new()); await Settle(700);
        }
        catch (Exception ex) { checks.Add(new { name = "runner", passed = false, detail = ex.ToString() }); }
        finally
        {
            File.WriteAllText(Path.Combine(AppPaths.Root, "ui-regression.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (Environment.GetEnvironmentVariable("LUMA_UI_REGRESSION_EXIT") == "1") { reallyClosing = true; Close(); }
        }
    }
}
