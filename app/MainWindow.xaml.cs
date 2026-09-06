using LumaMusic.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace LumaMusic;
public sealed partial class MainWindow : Window
{
    readonly LibraryService library=new();readonly OnlineService online=new();
    readonly Preferences prefs=AppPaths.Load();AudioService? audio;
    readonly ObservableCollection<Track> visible=[];readonly ObservableCollection<Track> queue=[];
    List<Track> tracks=[];List<AudioDevice> devices=[];Track? current;
    ContentDialog? activeDialog;
    // WinRT 同一时刻只允许一个 ContentDialog：开新框前自动关掉旧的，否则 COMException 直接闪退
    async Task<ContentDialogResult> Show(ContentDialog d){try{activeDialog?.Hide();}catch{/**/}activeDialog=d;try{return await d.ShowAsync();}finally{if(activeDialog==d)activeDialog=null;}}
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(150)};
    readonly DispatcherTimer seekTimer=new(){Interval=TimeSpan.FromMilliseconds(350)};
    readonly DispatcherTimer searchTimer=new(){Interval=TimeSpan.FromMilliseconds(220)};
    readonly SemaphoreSlim playGate=new(1,1);
    CancellationTokenSource mediaCancellation=new();CancellationTokenSource? scanCancellation;
    List<LyricLine> lyricLines=[];readonly List<Button> lyricButtons=[];
    PlaybackState lastState=new();bool ready,updatingSlider,seeking,showAlbums,favoritesOnly,nowVisible,closing,reallyClosing;
    string? albumFilter,artistFilter;long? playlistFilter;int playGeneration,lyricIndex=-2;double lyricOffset;
    DateTime endSeen=DateTime.MinValue,lastSave=DateTime.MinValue;string rawLyrics="";
    AudioDevice? Device=>devices.FirstOrDefault(d=>d.Id==prefs.DeviceId&&d.Backend==prefs.DeviceBackend);
    TrayIcon? tray;

    public MainWindow()
    {
        InitializeComponent();ExtendsContentIntoTitleBar=true;SetTitleBar(TitleBar);
        AppWindow.Resize(new SizeInt32(1320,860));AppWindow.Title="Luma Music";
        var icon=System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","Luma.ico");if(File.Exists(icon))AppWindow.SetIcon(icon);
        if(AppWindow.TitleBar!=null){AppWindow.TitleBar.ButtonBackgroundColor=Colors.Transparent;AppWindow.TitleBar.ButtonInactiveBackgroundColor=Colors.Transparent;AppWindow.TitleBar.ButtonForegroundColor=Color.FromArgb(255,210,225,210);}
        TrackList.ItemsSource=visible;QueueList.ItemsSource=queue;
        Root.Loaded+=Loaded;Root.KeyDown+=KeyDown;
        Root.SizeChanged+=(_,_)=>{LargeCover.Height=Math.Clamp((Root.ActualWidth-340)*.34,200,360);};
        timer.Tick+=Tick;searchTimer.Tick+=(_,_)=>{searchTimer.Stop();Filter();};
        seekTimer.Tick+=async(_,_)=>{seekTimer.Stop();if(!seeking)await SeekTo(SeekSlider.Value);};
        SeekSlider.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>{seeking=true;seekTimer.Stop();}),true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler(async(_,_)=>{if(seeking){seeking=false;seekTimer.Stop();await SeekTo(SeekSlider.Value);}}),true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent,new PointerEventHandler((_,_)=>{seeking=false;seekTimer.Stop();}),true);
        Closed+=async(_,_)=>{closing=true;timer.Stop();seekTimer.Stop();searchTimer.Stop();mediaCancellation.Cancel();scanCancellation?.Cancel();prefs.LastTrack=current?.Id??"";prefs.LastPosition=lastState.Position;prefs.Queue=queue.Select(t=>t.Id).ToList();AppPaths.Save(prefs);if(audio!=null){await audio.Stop();audio.Dispose();}tray?.Dispose();};
        AppWindow.Closing+=(_,args)=>{if(prefs.CloseToTray&&!reallyClosing){args.Cancel=true;HideToTray();}};
    }
    void HideToTray()
    {
        AppWindow.Hide();
        if(tray==null){
            var iconPath=System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","Luma.ico");
            tray=new TrayIcon(File.Exists(iconPath)?iconPath:"");
            tray.OpenRequested+=()=>{AppWindow.Show();};
            tray.ExitRequested+=()=>{reallyClosing=true;Close();};
        }
        tray.Add();
    }
    async Task AutoCheckUpdate()
    {
        try{
            await Task.Delay(3000);
            if(closing||(DateTime.UtcNow-prefs.LastUpdateCheck).TotalHours<24)return;
            var info=await UpdateChecker.Latest(CancellationToken.None);
            prefs.LastUpdateCheck=DateTime.UtcNow;AppPaths.Save(prefs);
            if(info!=null&&UpdateChecker.IsNewer(info.Tag)&&!closing)await OfferUpdate(info);
        }catch(Exception ex){AppPaths.Log("Update check: "+ex.Message);}
    }
    async Task OfferUpdate(UpdateChecker.UpdateInfo info)
    {
        var text=new TextBlock{Text=$"最新版本 {info.Tag}（当前 v{UpdateChecker.CurrentVersion}）\n\n"+(string.IsNullOrEmpty(info.Notes)?"确认后开始更新。":info.Notes),TextWrapping=TextWrapping.Wrap,MaxWidth=420};
        var dlg=Dialog("发现新版本",text,UpdateChecker.IsPackaged?"下载并安装":"前往下载");
        if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
        if(UpdateChecker.IsPackaged){
            try{
                SetBusy(true);Toast("正在下载更新…");
                var file=await UpdateChecker.DownloadPackage(info.Tag,CancellationToken.None);
                if(file==null){Toast("下载失败","发布资产里没有找到安装包，可前往发布页手动下载。",true);return;}
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file){UseShellExecute=true});
            }catch(Exception ex){Toast("下载更新失败",ex.Message,true);}
            finally{SetBusy(false);}
        }
        else Windows.System.Launcher.LaunchUriAsync(new Uri(info.Url));
    }
    void ApplyTheme(){
        Root.RequestedTheme=prefs.Theme switch{1=>ElementTheme.Dark,2=>ElementTheme.Light,_=>ElementTheme.Default};
        bool light=Root.ActualTheme==ElementTheme.Light;
        if(AppWindow.TitleBar!=null){AppWindow.TitleBar.ButtonForegroundColor=light?Color.FromArgb(255,40,48,44):Color.FromArgb(255,210,225,210);AppWindow.TitleBar.ButtonInactiveForegroundColor=light?Color.FromArgb(255,140,150,146):Color.FromArgb(255,150,165,155);}
        ApplyAccent(lastPalette);
    }
    Color[] lastPalette=[];
    SolidColorBrush AccentBrush()=>AccentDict()["Accent"] as SolidColorBrush ?? new SolidColorBrush(OnAmbient(255));
    ResourceDictionary AccentDict()=>(ResourceDictionary)Application.Current.Resources.ThemeDictionaries[LightTheme?"Light":"Default"];
    // 强调色随封面主色走：浅色主题加深保对比，深色主题提亮保存在感。没有封面时用默认中性色。
    void ApplyAccent(Color[] palette)
    {
        lastPalette=palette;
        var dict=AccentDict();
        if(dict?["Accent"] is not SolidColorBrush brush||palette.Length==0){if(dict?["Accent"] is SolidColorBrush reset)reset.Color=LightTheme?Color.FromArgb(255,51,48,43):Color.FromArgb(255,227,224,213);return;}
        var c=palette[0];
        var adjusted=LightTheme?Color.FromArgb(255,(byte)(c.R*52/100+28),(byte)(c.G*52/100+28),(byte)(c.B*52/100+28)):Color.FromArgb(255,(byte)(c.R*55/100+118),(byte)(c.G*55/100+118),(byte)(c.B*55/100+118));
        brush.Color=adjusted;
    }
    bool LightTheme=>Root.ActualTheme==ElementTheme.Light;
    Color OnAmbient(byte a)=>LightTheme?Color.FromArgb(a,26,32,29):Color.FromArgb(a,255,255,255);
    SolidColorBrush InactiveLyric()=>LightTheme?Brush(150,45,55,50):Brush(120,194,210,193);
    SolidColorBrush ActiveLyric()=>new(OnAmbient(255));
    async void Loaded(object sender,RoutedEventArgs e)
    {
        if(ready)return;
        ApplyTheme();Ambient.ThemeMode=prefs.Theme==2?"light":"dark";Ambient.Aurora=prefs.Aurora&&!prefs.ReducedMotion;Ambient.ApplyAurora();Ambient.PaletteChanged+=c=>DispatcherQueue.TryEnqueue(()=>ApplyAccent(c));
        try{audio=new AudioService();devices=audio.Devices();if(Device==null){var d=devices.FirstOrDefault(x=>x.Default)??devices.FirstOrDefault();if(d!=null){prefs.DeviceId=d.Id;prefs.DeviceBackend=d.Backend;}}}
        catch(Exception ex){Toast("音频引擎无法启动",ex.Message,true);}
        tracks=await Task.Run(library.Load);foreach(var id in prefs.Queue){var t=tracks.FirstOrDefault(t=>t.Id==id);if(t!=null)queue.Add(t);}
        VolumeSlider.Value=prefs.Volume*100;Ambient.Reduced=prefs.ReducedMotion;
        ready=true;Filter();RefreshPlaylists();SetModeIcons();timer.Start();
        if(tracks.FirstOrDefault(t=>t.Id==prefs.LastTrack) is {} last){current=last;await ShowTrack(last);}
        var arguments=Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();if(arguments.Count>0)await Import(arguments);
        _=AutoCheckUpdate();
    }
    void Toast(string title,string message="",bool error=false){if(closing)return;Notice.Title=title;Notice.Message=message;Notice.Severity=error?InfoBarSeverity.Warning:InfoBarSeverity.Informational;Notice.IsOpen=true;}
    void SetBusy(bool busy){BusyBar.Visibility=busy?Visibility.Visible:Visibility.Collapsed;}
    static SolidColorBrush Brush(byte a,byte r,byte g,byte b)=>new(Color.FromArgb(a,r,g,b));
    static string Time(double seconds)=>TimeSpan.FromSeconds(Math.Max(0,seconds)).ToString(@"m\:ss");
    void Filter()
    {
        if(!ready)return;IEnumerable<Track> source=tracks;
        if(favoritesOnly)source=source.Where(t=>t.Favorite);
        if(albumFilter!=null)source=source.Where(t=>t.AlbumKey==albumFilter);
        if(artistFilter!=null)source=source.Where(t=>t.Artist==artistFilter);
        if(playlistFilter is long id){var ids=library.PlaylistTracks(id);source=source.Where(t=>ids.Contains(t.Id));}
        var search=SearchBox.Text.Trim();if(search.Length>0)source=source.Where(t=>($"{t.Title} {t.Artist} {t.Album}").Contains(search,StringComparison.CurrentCultureIgnoreCase));
        visible.Clear();foreach(var t in source)visible.Add(t);
        TrackCount.Text=$"{visible.Count:N0} 首歌曲";LibraryCount.Text=$"{tracks.Select(t=>t.AlbumKey).Distinct().Count():N0} 张专辑 · {tracks.Count:N0} 首歌曲";
        EmptyState.Visibility=visible.Count==0?Visibility.Visible:Visibility.Collapsed;
        EmptyTitle.Text=tracks.Count==0?"你的音乐，值得一个好位置。":"暂时没有匹配的歌曲";
        TrackList.Visibility=showAlbums?Visibility.Collapsed:Visibility.Visible;AlbumGrid.Visibility=showAlbums?Visibility.Visible:Visibility.Collapsed;
        AlbumGrid.ItemsSource=visible.GroupBy(t=>t.AlbumKey).Select(g=>g.First()).ToList();
    }
    void Animate(UIElement element){if(prefs.ReducedMotion)return;var v=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);var c=v.Compositor;var fade=c.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(0,0);fade.InsertKeyFrame(1,1);fade.Duration=TimeSpan.FromMilliseconds(300);v.StartAnimation("Opacity",fade);var slide=c.CreateVector3KeyFrameAnimation();slide.InsertKeyFrame(0,new(0,12,0));slide.InsertKeyFrame(1,Vector3.Zero);slide.Duration=TimeSpan.FromMilliseconds(350);Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element,true);v.StartAnimation("Translation",slide);}
    void Fade(UIElement element,bool show){var v=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);float to=show?1:0;if(prefs.ReducedMotion){v.StopAnimation("Opacity");v.Opacity=to;return;}var c=v.Compositor;var anim=c.CreateScalarKeyFrameAnimation();anim.InsertKeyFrame(0,v.Opacity);anim.InsertKeyFrame(1,to);anim.Duration=TimeSpan.FromMilliseconds(650);v.StartAnimation("Opacity",anim);}
    void ShowLibrary(){nowVisible=false;NowPage.Visibility=Visibility.Collapsed;LibraryPage.Visibility=Visibility.Visible;QueuePanel.Visibility=Visibility.Collapsed;Animate(LibraryPage);}
    void Library_Click(object sender,RoutedEventArgs e){favoritesOnly=false;albumFilter=null;artistFilter=null;playlistFilter=null;PageTitle.Text="音乐库";ShowLibrary();Filter();}
    void Favorites_Click(object sender,RoutedEventArgs e){favoritesOnly=true;albumFilter=null;artistFilter=null;playlistFilter=null;PageTitle.Text="我的收藏";ShowLibrary();Filter();}
    void Now_Click(object sender,RoutedEventArgs e){if(nowVisible)return;ConnectedAnimation? animation=null;if(!prefs.ReducedMotion&&current!=null)animation=ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("cover",MiniCover);nowVisible=true;LibraryPage.Visibility=Visibility.Collapsed;NowPage.Visibility=Visibility.Visible;NowPage.UpdateLayout();Animate(NowPage);animation?.TryStart(LargeCover);}
    void SongsTab_Click(object sender,RoutedEventArgs e){showAlbums=false;Filter();}
    void AlbumsTab_Click(object sender,RoutedEventArgs e){showAlbums=true;Filter();}
    void Search_Changed(AutoSuggestBox sender,AutoSuggestBoxTextChangedEventArgs args){searchTimer.Stop();searchTimer.Start();}
    void Album_ItemClick(object sender,ItemClickEventArgs e){if(e.ClickedItem is Track t){albumFilter=t.AlbumKey;showAlbums=false;PageTitle.Text=t.Album;Filter();}}
    async void ArtistsTab_Click(object sender,RoutedEventArgs e){var list=new ListView{ItemsSource=tracks.Select(t=>t.Artist).Distinct().Order().ToList(),SelectionMode=ListViewSelectionMode.Single,MaxHeight=380};var dialog=Dialog("选择艺术家",list,"查看歌曲");if(await Show(dialog)==ContentDialogResult.Primary&&list.SelectedItem is string name){artistFilter=name;albumFilter=null;showAlbums=false;PageTitle.Text=name;Filter();}}
    ContentDialog Dialog(string title,object content,string primary="确定")=>new(){Title=title,Content=content,PrimaryButtonText=primary,CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary,XamlRoot=Root.XamlRoot,RequestedTheme=ElementTheme.Dark};
    void RefreshPlaylists(){PlaylistNav.Children.Clear();foreach(var p in library.Playlists()){var b=new Button{Content="♫  "+p.Name,Style=(Style)Application.Current.Resources["NavButton"],Tag=p};
        // 右键删除：确认后连歌单关系一并清除，歌曲文件不受影响；删的是当前筛选中的列表则回到音乐库
        var menu=new MenuFlyout();var del=new MenuFlyoutItem{Text="删除播放列表"};del.Click+=async(_,_)=>{
            var confirm=new TextBlock{Text="「"+p.Name+"」将被删除，其中的歌曲文件不受影响。",TextWrapping=TextWrapping.Wrap};
            if(await Show(Dialog("删除播放列表",confirm,"删除"))==ContentDialogResult.Primary){library.DeletePlaylist(p.Id);if(playlistFilter==p.Id){playlistFilter=null;PageTitle.Text="音乐库";ShowLibrary();}Filter();RefreshPlaylists();Toast("播放列表已删除",p.Name);}
        };menu.Items.Add(del);b.ContextFlyout=menu;
        b.Click+=(_,_)=>{playlistFilter=p.Id;favoritesOnly=false;albumFilter=null;artistFilter=null;PageTitle.Text=p.Name;ShowLibrary();Filter();};PlaylistNav.Children.Add(b);}}
    async void NewPlaylist_Click(object sender,RoutedEventArgs e){var text=new TextBox{PlaceholderText="为这份心情取个名字",MaxLength=60};if(await Show(Dialog("新建播放列表",text,"创建"))==ContentDialogResult.Primary&&!string.IsNullOrWhiteSpace(text.Text)){library.CreatePlaylist(text.Text.Trim());RefreshPlaylists();}}
    async void ImportFolder_Click(object sender,RoutedEventArgs e){var picker=new FolderPicker();WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));picker.FileTypeFilter.Add("*");var folder=await picker.PickSingleFolderAsync();if(folder!=null){if(!prefs.Roots.Contains(folder.Path))prefs.Roots.Add(folder.Path);AppPaths.Save(prefs);await Import([folder.Path]);}}
    async Task Import(IEnumerable<string> paths){if(scanCancellation!=null){Toast("正在扫描音乐，请稍候");return;}scanCancellation=new();SetBusy(true);try{var count=await library.Import(paths,new Progress<string>(s=>{Notice.Title="正在整理音乐库";Notice.Message=s;Notice.IsOpen=true;}),scanCancellation.Token);tracks=await Task.Run(library.Load);Filter();Toast("音乐库已更新",$"新增或更新 {count} 首歌曲");}catch(OperationCanceledException){}catch(Exception ex){Toast("导入未完成",ex.Message,true);}finally{scanCancellation?.Dispose();scanCancellation=null;SetBusy(false);}}
    void Root_DragOver(object sender,DragEventArgs e){if(e.DataView.Contains(StandardDataFormats.StorageItems)){e.AcceptedOperation=DataPackageOperation.Copy;e.DragUIOverride.Caption="添加到 Luma 音乐库";}}
    async void Root_Drop(object sender,DragEventArgs e){if(e.DataView.Contains(StandardDataFormats.StorageItems)){var items=await e.DataView.GetStorageItemsAsync();await Import(items.Select(i=>i.Path));}}
    async void Track_ItemClick(object sender,ItemClickEventArgs e){if(e.ClickedItem is Track t){if(!queue.Contains(t)){queue.Clear();foreach(var item in visible)queue.Add(item);}await Play(t);}}
    async void PlayAll_Click(object sender,RoutedEventArgs e){if(visible.Count==0){ImportFolder_Click(sender,e);return;}queue.Clear();foreach(var t in visible)queue.Add(t);await Play(queue[0]);}
    async Task Play(Track track,double position=0)
    {
        if(audio==null){Toast("音频引擎不可用",error:true);return;}if(Device==null){await Settings();return;}
        int generation=++playGeneration;await playGate.WaitAsync();
        try{
            if(generation!=playGeneration)return;SetBusy(true);Notice.IsOpen=false;endSeen=DateTime.MinValue;
            // 历史档案可能给 WASAPI 设备存过 ASIO 后端，播放前按设备实际类型纠正。
            var profile=prefs.Profile;if(Device.Backend!=2&&profile.Backend==2)profile.Backend=Device.Backend;
            await audio.Open(track,Device,profile,prefs.Volume,position);
            if(FlexAsioConfig.LastWarning.Length>0)Toast("ASIO 提示",FlexAsioConfig.LastWarning);
            current=track;lastState=audio.State();prefs.LastTrack=track.Id;
            if(!queue.Any(t=>t.Id==track.Id))queue.Add(track);
            PlayIcon.Glyph="\uE769";SetBusy(false);await ShowTrack(track);AppPaths.Save(prefs);
        }catch(Exception ex){SetBusy(false);PlayIcon.Glyph="\uE768";Toast("暂时无法播放",ex.Message,true);
            if(!closing){var content=new TextBlock{Text=ex.Message+"\n\n可以使用 PCM 兼容输出；必要时将多声道混成立体声。此选择会保存到当前设备。",TextWrapping=TextWrapping.Wrap,MaxWidth=420};var dlg=Dialog("输出格式需要调整",content,"使用 PCM 兼容播放");dlg.SecondaryButtonText="打开输出设置";
                var choice=await Show(dlg);
                if(choice==ContentDialogResult.Primary&&Device!=null){var p=prefs.Profile;p.DsdMode=0;p.PcmRate=176400;p.ForceRate=Device.Rate>0?Device.Rate:44100;p.Downmix=track.Channels>(Device.Channels>0?Device.Channels:2);AppPaths.Save(prefs);DispatcherQueue.TryEnqueue(async()=>await Play(track,position));}
                else if(choice==ContentDialogResult.Secondary)DispatcherQueue.TryEnqueue(async()=>await Settings());
            }
        }finally{SetBusy(false);playGate.Release();}
    }
    async Task ShowTrack(Track t){NowTitle.Text=MiniTitle.Text=t.Title;NowArtist.Text=MiniArtist.Text=t.Artist;NowAlbum.Text=t.Album;CurrentHeart.Glyph=t.FavoriteGlyph;Total.Text=t.DurationText;await SetCover(t.Cover);mediaCancellation.Cancel();mediaCancellation.Dispose();mediaCancellation=new();lyricIndex=-2;_ = LoadMedia(t,mediaCancellation.Token);}
    async Task SetCover(string file)
    {
        Ambient.ThemeMode=prefs.Theme==2?"light":"dark";
        Ambient.SetSource(!string.IsNullOrEmpty(file)&&File.Exists(file)?file:null);
        AttachReflection(!string.IsNullOrEmpty(file)&&File.Exists(file)?file:null);
        if(!string.IsNullOrEmpty(file)&&File.Exists(file)){var image=new BitmapImage(new Uri(file));CoverImage.Source=MiniCoverImage.Source=image;try{var f=await StorageFile.GetFileFromPathAsync(file);using var s=await f.OpenReadAsync();var d=await BitmapDecoder.CreateAsync(s);var data=await d.GetPixelDataAsync(BitmapPixelFormat.Rgba8,BitmapAlphaMode.Ignore,new BitmapTransform{ScaledWidth=32,ScaledHeight=32},ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage);var bytes=data.DetachPixelData();long r=0,g=0,b=0;for(int i=0;i<bytes.Length;i+=4){r+=bytes[i];g+=bytes[i+1];b+=bytes[i+2];}int n=bytes.Length/4;Ambient.SetTint(Color.FromArgb(255,(byte)Math.Clamp(r/n,35,180),(byte)Math.Clamp(g/n,35,180),(byte)Math.Clamp(b/n,35,180)));}catch{}}
        else{CoverImage.Source=MiniCoverImage.Source=null;Ambient.SetTint(Color.FromArgb(255,90,129,114));}
    }
    // Now 页封面倒影：翻转的封面表面 + 向下渐隐遮罩，纯合成层无额外解码。
    void AttachReflection(string? coverFile)
    {
        try{
            var host=CoverReflection;
            if(coverFile==null){Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host,null);return;}
            var compositor=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host).Compositor;
            var surface=Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(coverFile));
            var surfaceBrush=compositor.CreateSurfaceBrush(surface);
            surfaceBrush.Stretch=Microsoft.UI.Composition.CompositionStretch.UniformToFill;
            surfaceBrush.TransformMatrix=System.Numerics.Matrix3x2.CreateScale(1,-1);
            var fade=compositor.CreateLinearGradientBrush();
            fade.StartPoint=new System.Numerics.Vector2(0,0);fade.EndPoint=new System.Numerics.Vector2(0,1);
            fade.ColorStops.Add(compositor.CreateColorGradientStop(0f,Color.FromArgb(170,255,255,255)));
            fade.ColorStops.Add(compositor.CreateColorGradientStop(1f,Colors.Transparent));
            var mask=compositor.CreateMaskBrush();
            mask.Source=surfaceBrush;mask.Mask=fade;
            var sprite=compositor.CreateSpriteVisual();
            sprite.Brush=mask;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host,sprite);
            host.SizeChanged+=(h,_)=>{var b=(Border)h;sprite.Size=new System.Numerics.Vector2((float)b.ActualWidth,(float)b.ActualHeight);};
            sprite.Size=new System.Numerics.Vector2((float)host.ActualWidth,(float)host.ActualHeight);
        }catch(Exception e){AppPaths.Log("Reflection: "+e.Message);}
    }
    async Task LoadMedia(Track t,CancellationToken token)
    {
        try{
            var cached=library.Lyrics(t.Id);lyricOffset=cached.Offset;string text=cached.Text;
            if(string.IsNullOrEmpty(text))text=await LyricsService.Local(t);token.ThrowIfCancellationRequested();DisplayLyrics(text);
            if(prefs.Online&&string.IsNullOrWhiteSpace(text)){LyricsPlaceholder.Text="正在寻找这首歌的歌词…";var found=await online.Lyrics(t,token);token.ThrowIfCancellationRequested();if(!string.IsNullOrWhiteSpace(found)){library.SaveLyrics(t.Id,found);DisplayLyrics(found);}else LyricsPlaceholder.Text="还没找到歌词。\n你可以导入本地 LRC。";}
            if(prefs.Online&&string.IsNullOrEmpty(t.Cover)){var covers=await online.Covers(t,token);var match=covers.FirstOrDefault(c=>c.Title.Equals(t.Album,StringComparison.OrdinalIgnoreCase)&&c.Artist.Equals(t.Artist,StringComparison.OrdinalIgnoreCase));if(match!=null){var path=await online.DownloadCover(match,token);token.ThrowIfCancellationRequested();if(path!=null){t.Cover=path;library.Save(t);await SetCover(path);}}}
        }catch(OperationCanceledException){}catch(Exception ex){AppPaths.Log("Media lookup: "+ex.Message);if(current?.Id==t.Id&&rawLyrics.Length==0)LyricsPlaceholder.Text="暂时无法连接歌词服务。\n本地播放不受影响。";}
    }
    void DisplayLyrics(string text)
    {
        rawLyrics=text;lyricLines=LyricsService.Parse(text);lyricIndex=-2;LyricsPanel.Children.Clear();lyricButtons.Clear();
        if(lyricLines.Count>0){LyricsPlaceholder.Visibility=Visibility.Collapsed;foreach(var l in lyricLines){var label=new TextBlock{Text=l.Text.Length==0?"♪":l.Text,TextWrapping=TextWrapping.Wrap,FontSize=22,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Foreground=InactiveLyric()};var b=new Button{Content=label,Background=new SolidColorBrush(Colors.Transparent),BorderThickness=new Thickness(0),Padding=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Opacity=.45};b.Click+=async(_,_)=>await SeekTo(Math.Max(0,l.Time-lyricOffset));LyricsPanel.Children.Add(b);lyricButtons.Add(b);}}
        else{LyricsPlaceholder.Visibility=Visibility.Visible;LyricsPlaceholder.Text=string.IsNullOrWhiteSpace(text)?"歌词会在这里，随着音乐浮现。":text;LyricsPlaceholder.FontSize=string.IsNullOrWhiteSpace(text)?21:16;if(text.Length>300){LyricsPlaceholder.Visibility=Visibility.Collapsed;LyricsPanel.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=20,LineHeight=37,Foreground=InactiveLyric()});}}
    }
    async void Tick(object? sender,object e)
    {
        if(audio==null||audio.Busy||closing)return;
        lastState=audio.State();var state=lastState;
        if(state.Failed){await audio.Stop();PlayIcon.Glyph="\uE768";Toast("播放已停止",state.Error.Length>0?state.Error:"音频设备断开或驱动状态发生变化。请重新选择输出设备。",true);return;}
        if(state.Duration>0){SignalPath.Text=state.Chain;VolumeSlider.IsEnabled=!(state.Dsd&&state.Mode!=0);Elapsed.Text=Time(state.Position);Total.Text=Time(state.Duration);if(!seeking&&!seekTimer.IsEnabled){updatingSlider=true;SeekSlider.Maximum=state.Duration;SeekSlider.Value=state.Position;updatingSlider=false;}PlayIcon.Glyph=state.Playing?"\uE769":"\uE768";
            int index=LyricsService.Current(lyricLines,state.Position+lyricOffset);if(index!=lyricIndex){if(lyricIndex>=0&&lyricIndex<lyricButtons.Count)((TextBlock)lyricButtons[lyricIndex].Content).Foreground=InactiveLyric();lyricIndex=index;if(index>=0&&index<lyricButtons.Count){var b=lyricButtons[index];((TextBlock)b.Content).Foreground=ActiveLyric();if(nowVisible){var point=b.TransformToVisual(LyricsPanel).TransformPoint(new(0,0));LyricsScroll.ChangeView(null,Math.Max(0,point.Y-LyricsScroll.ActualHeight*.36),null,prefs.ReducedMotion);}}
            // 参考玻璃风格：当前行全亮，其余按距离衰减透明度，像蒙在毛玻璃后面。
            for(int i=0;i<lyricButtons.Count;i++){int dist=Math.Abs(i-index);float o=dist==0?1f:dist==1?.5f:dist==2?.3f:.16f;var v=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(lyricButtons[i]);v.StopAnimation("Opacity");v.Opacity=o;}}
            if(DateTime.UtcNow-lastSave>TimeSpan.FromSeconds(15)){lastSave=DateTime.UtcNow;prefs.LastPosition=state.Position;prefs.Queue=queue.Select(t=>t.Id).ToList();AppPaths.Save(prefs);}
        }
        if(state.Ended&&state.Playing){if(endSeen==DateTime.MinValue)endSeen=DateTime.UtcNow;else if(DateTime.UtcNow-endSeen>TimeSpan.FromMilliseconds(220)){endSeen=DateTime.MinValue;await Advance(1,true);}}
    }
    async void PlayPause_Click(object sender,RoutedEventArgs e){if(audio==null||audio.Busy)return;if(lastState.Duration>0){audio.Pause(lastState.Playing);PlayIcon.Glyph=lastState.Playing?"\uE768":"\uE769";}else if(current!=null)await Play(current,prefs.LastPosition);else if(visible.Count>0)await Play(visible[0]);else ImportFolder_Click(sender,e);}
    async Task Advance(int direction,bool ended=false){if(current==null)return;if(prefs.Repeat==2&&ended){await Play(current);return;}var list=queue.Count>0?queue.ToList():visible.ToList();if(list.Count==0)return;int i=list.FindIndex(t=>t.Id==current.Id);int next=prefs.Shuffle&&list.Count>1?(i+Random.Shared.Next(1,list.Count))%list.Count:i+direction;if(next>=list.Count){if(prefs.Repeat==1)next=0;else{if(audio!=null)await audio.Stop();PlayIcon.Glyph="\uE768";return;}}if(next<0)next=list.Count-1;await Play(list[next]);}
    async void Previous_Click(object sender,RoutedEventArgs e){if(lastState.Position>3)await SeekTo(0);else await Advance(-1);}
    async void Next_Click(object sender,RoutedEventArgs e)=>await Advance(1);
    async Task SeekTo(double position){if(current==null||audio==null||audio.Busy)return;bool wasPaused=!lastState.Playing;await Play(current,Math.Clamp(position,0,Math.Max(0,current.Duration-.05)));if(wasPaused)audio.Pause(true);}
    void Seek_Changed(object sender,RangeBaseValueChangedEventArgs e){if(!ready||updatingSlider)return;Elapsed.Text=Time(e.NewValue);if(!seeking){seekTimer.Stop();seekTimer.Start();}}
    void Volume_Changed(object sender,RangeBaseValueChangedEventArgs e){if(!ready)return;prefs.Volume=(float)e.NewValue/100;audio?.Volume(prefs.Volume);}
    void SetModeIcons(){ShuffleIcon.Foreground=prefs.Shuffle?AccentBrush():AccentDict()["TextSecondary"] as SolidColorBrush ?? new SolidColorBrush(OnAmbient(160));RepeatIcon.Foreground=prefs.Repeat>0?AccentBrush():AccentDict()["TextSecondary"] as SolidColorBrush ?? new SolidColorBrush(OnAmbient(160));RepeatIcon.Glyph=prefs.Repeat==2?"\uE8ED":"\uE8EE";}
    void Shuffle_Click(object sender,RoutedEventArgs e){prefs.Shuffle=!prefs.Shuffle;SetModeIcons();AppPaths.Save(prefs);}
    void Repeat_Click(object sender,RoutedEventArgs e){prefs.Repeat=(prefs.Repeat+1)%3;SetModeIcons();AppPaths.Save(prefs);Toast(prefs.Repeat==0?"顺序播放":prefs.Repeat==1?"列表循环":"单曲循环");}
    void Favorite(Track t){t.Favorite=!t.Favorite;library.Save(t);if(current?.Id==t.Id)CurrentHeart.Glyph=t.FavoriteGlyph;}
    void FavoriteTrack_Click(object sender,RoutedEventArgs e){if(sender is Button{Tag:Track t})Favorite(t);}
    void FavoriteCurrent_Click(object sender,RoutedEventArgs e){if(current!=null)Favorite(current);}
    void Queue_Click(object sender,RoutedEventArgs e){bool open=QueuePanel.Visibility!=Visibility.Visible;QueuePanel.Visibility=open?Visibility.Visible:Visibility.Collapsed;if(open)Animate(QueuePanel);}
    async void Queue_ItemClick(object sender,ItemClickEventArgs e){if(e.ClickedItem is Track t)await Play(t);}
    void QueueUp_Click(object sender,RoutedEventArgs e){int i=QueueList.SelectedIndex;if(i>0){queue.Move(i,i-1);QueueList.SelectedIndex=i-1;}}
    void QueueRemove_Click(object sender,RoutedEventArgs e){int i=QueueList.SelectedIndex;if(i>=0)queue.RemoveAt(i);}
    void TrackMenu_Click(object sender,RoutedEventArgs e){if(sender is not Button{Tag:Track t} button)return;var menu=new MenuFlyout();
if(playlistFilter is long pid){var rm=new MenuFlyoutItem{Text="从播放列表中移除"};rm.Click+=(_,_)=>{library.RemoveFromPlaylist(pid,t.Id);Filter();Toast("已从播放列表移除",t.Title);};menu.Items.Add(rm);}
var next=new MenuFlyoutItem{Text="下一首播放"};next.Click+=(_,_)=>{int index=current==null?-1:queue.ToList().FindIndex(q=>q.Id==current.Id);queue.Insert(Math.Clamp(index+1,0,queue.Count),t);Toast("已加入下一首",t.Title);};menu.Items.Add(next);var sub=new MenuFlyoutSubItem{Text="添加到播放列表"};foreach(var p in library.Playlists()){var item=new MenuFlyoutItem{Text=p.Name};item.Click+=(_,_)=>{library.AddToPlaylist(p.Id,t.Id);Toast("已添加到 "+p.Name);};sub.Items.Add(item);}menu.Items.Add(sub);var locate=new MenuFlyoutItem{Text="在资源管理器中显示"};locate.Click+=(_,_)=>{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe"){Arguments="/select,\""+t.Path+"\"",UseShellExecute=true});};menu.Items.Add(locate);menu.ShowAt(button);}
    async void Settings_Click(object sender,RoutedEventArgs e)=>await Settings();
    async Task Settings()
    {
        if(audio==null)return;if(audio.Busy){Toast("正在切换音频，请稍候");return;}devices=audio.Devices();
        var deviceBox=new ComboBox{Header="输出设备",ItemsSource=devices,SelectedItem=Device??devices.FirstOrDefault(),HorizontalAlignment=HorizontalAlignment.Stretch};
        var modeBox=new ComboBox{Header="输出方式",HorizontalAlignment=HorizontalAlignment.Stretch};
        // 开箱即用的 ASIO：系统无任何 ASIO 驱动时，提供随包分发的 FlexASIO 一键安装（GPL，见 THIRD-PARTY-NOTICES.md）
        var asioHint=new TextBlock{FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=Brush(200,172,194,176),Visibility=Visibility.Collapsed};
        var asioInstall=new Button{Content="一键安装 FlexASIO 通用驱动（WASAPI 独占，需管理员确认）",HorizontalAlignment=HorizontalAlignment.Left,Visibility=Visibility.Collapsed};
        // FlexASIO 是通用壳，需定向到具体输出设备；端点 ID 作身份记忆，名字交给 FlexASIO 匹配
        var asioTarget=new ComboBox{Header="FlexASIO 输出目标（WASAPI 设备）",HorizontalAlignment=HorizontalAlignment.Stretch,Visibility=Visibility.Collapsed,SelectedValuePath="Id",DisplayMemberPath="Name",ItemsSource=new AudioDevice[]{new(-1,1,"（Windows 默认输出）","",false,0,0)}.Concat(devices.Where(d=>d.Backend==1)).ToList()};
        var dsdBox=new ComboBox{Header="DSD 播放方式",ItemsSource=new[]{"DSD 转 PCM","DoP 透传","ASIO 原生 DSD","播放前选择"},SelectedIndex=prefs.Profile.DsdMode,HorizontalAlignment=HorizontalAlignment.Stretch};
        var dop=new CheckBox{Content="我已确认此 DAC 支持 DoP",IsChecked=prefs.Profile.DopConfirmed};
        int[] rates=[0,44100,48000,88200,96000,176400,192000,352800,384000];var rate=new ComboBox{Header="PCM 输出采样率",ItemsSource=rates.Select(r=>r==0?"跟随音源":$"{r/1000d:0.#} kHz").ToList(),SelectedIndex=Math.Max(0,Array.IndexOf(rates,prefs.Profile.ForceRate)),HorizontalAlignment=HorizontalAlignment.Stretch};
        var downmix=new CheckBox{Content="将多声道 PCM 混成立体声",IsChecked=prefs.Profile.Downmix};
        var mapping=new TextBox{Header="ASIO 通道映射（从 1 开始，按 FL FR C LFE SL SR BL BR 顺序）",Text=string.Join(", ",prefs.Profile.Mapping.Select(i=>i+1))};
        var onlineCheck=new ToggleSwitch{Header="联网查找缺失封面与歌词",IsOn=prefs.Online,OnContent="开启",OffContent="仅本地"};
        var themeBox=new ComboBox{Header="主题",ItemsSource=new[]{"跟随系统","深色","浅色"},SelectedIndex=Math.Clamp(prefs.Theme,0,2),HorizontalAlignment=HorizontalAlignment.Stretch};
        var auroraCheck=new ToggleSwitch{Header="背景动态流光（随封面取色）",IsOn=prefs.Aurora,OnContent="开启",OffContent="关闭"};
        var motion=new ToggleSwitch{Header="减少动态效果",IsOn=prefs.ReducedMotion,OnContent="开启",OffContent="关闭"};
        var closeBehavior=new RadioButtons{Header="关闭窗口时",ItemsSource=new[]{"直接退出","最小化到系统托盘"},SelectedIndex=prefs.CloseToTray?1:0,Margin=new Thickness(0,4,0,0)};
        // 关于：版本号 + 手动检查更新（启动时也会自动检查一次，24 小时节流）
        var checkStatus=new TextBlock{FontSize=11,Foreground=Brush(200,172,194,176),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
        var checkUpdate=new Button{Content="检查更新",Padding=new Thickness(14,5,14,5)};
        var about=new StackPanel{Spacing=7};
        about.Children.Add(new TextBlock{Text=$"LumaMusic  v{UpdateChecker.CurrentVersion}",FontSize=13,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        var checkRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};checkRow.Children.Add(checkUpdate);checkRow.Children.Add(checkStatus);about.Children.Add(checkRow);
        checkUpdate.Click+=async(_,_)=>{
            checkUpdate.IsEnabled=false;checkStatus.Text="正在检查更新…";
            try{
                var info=await UpdateChecker.Latest(CancellationToken.None);
                if(info==null)checkStatus.Text="无法连接更新服务，稍后再试。";
                else if(!UpdateChecker.IsNewer(info.Tag)){checkStatus.Text="已是最新版本。";prefs.LastUpdateCheck=DateTime.UtcNow;AppPaths.Save(prefs);}
                else{checkStatus.Text=$"发现新版本 {info.Tag}";await OfferUpdate(info);checkStatus.Text="";}
            }catch(Exception ex){checkStatus.Text="检查失败："+ex.Message;}
            finally{checkUpdate.IsEnabled=true;}
        };
        var hint=new TextBlock{Text="独占模式会占用所选设备。DSD 透传时软件音量不可用，请使用 DAC 控制音量。",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brush(200,172,194,176)};
        var scan=new Button{Content="重新扫描已添加的音乐文件夹"};scan.Click+=async(_,_)=>await Import(prefs.Roots);
        void LoadProfile(AudioDevice d){
            var key=$"{d.Backend}:{d.Id}";var p=prefs.Profiles.TryGetValue(key,out var profile)?profile:new DeviceProfile{Backend=d.Backend};
            // 输出方式跟随设备类型：WASAPI 设备只有共享/独占，ASIO 驱动只有 ASIO，两者不可交叉。
            modeBox.ItemsSource=d.Backend==2?new object[]{"ASIO"}:new object[]{"WASAPI 共享","WASAPI 独占"};
            modeBox.SelectedIndex=d.Backend==2?0:Math.Clamp(p.Backend,0,1);
            dsdBox.SelectedIndex=p.DsdMode;dop.IsChecked=p.DopConfirmed;downmix.IsChecked=p.Downmix;rate.SelectedIndex=Math.Max(0,Array.IndexOf(rates,p.ForceRate));mapping.Text=string.Join(", ",p.Mapping.Select(i=>i+1));asioTarget.Visibility=d.Name==AsioSetup.DriverName?Visibility.Visible:Visibility.Collapsed;asioTarget.SelectedValue=p.AsioTargetId;
        }
        deviceBox.SelectionChanged+=(_,_)=>{if(deviceBox.SelectedItem is AudioDevice d)LoadProfile(d);};
        // 是否提供 FlexASIO 一键安装只看系统里有没有 ASIO 驱动，与输出方式无关。
        void RefreshAsioState(){bool show=!devices.Any(d=>d.Backend==2);asioHint.Text=show?"未检测到系统中的 ASIO 驱动。如需以 ASIO 独占方式输出（FlexASIO 可定向到指定设备），可一键安装随应用附带的 FlexASIO 通用驱动（GPL 开源）。":"";asioHint.Visibility=asioInstall.Visibility=show?Visibility.Visible:Visibility.Collapsed;}
        asioInstall.Click+=async(_,_)=>{
            asioInstall.IsEnabled=false;asioHint.Text="正在安装 FlexASIO 驱动…请在弹出的管理员授权窗口选择“是”。";
            try{
                var (ok,err)=await AsioSetup.InstallAsync();
                if(!ok){asioHint.Text=err;return;}
                devices=audio.Devices();deviceBox.ItemsSource=devices;
                if(devices.FirstOrDefault(d=>d.Backend==2) is AudioDevice flex){deviceBox.SelectedItem=flex;Toast("FlexASIO 驱动已安装","已自动选中，输出方式为 ASIO");}
                RefreshAsioState();
            }catch(Exception ex){asioHint.Text="安装失败："+ex.Message;AppPaths.Log("AsioSetup UI: "+ex.Message);}
            finally{asioInstall.IsEnabled=true;}
        };
        RefreshAsioState();
        if(deviceBox.SelectedItem is AudioDevice d0)LoadProfile(d0);
        var panel=new StackPanel{Spacing=15,Width=470};foreach(var el in new UIElement[]{deviceBox,modeBox,asioHint,asioInstall,asioTarget,dsdBox,dop,rate,downmix,mapping,hint,onlineCheck,themeBox,auroraCheck,motion,closeBehavior,scan,about})panel.Children.Add(el);
        var dialog=Dialog("设置",new ScrollViewer{Content=panel,MaxHeight=530},"保存设置");
        dialog.PrimaryButtonClick+=async(_,args)=>{
            if(deviceBox.SelectedItem is not AudioDevice d){args.Cancel=true;hint.Text="请选择音频设备。";return;}
            // 后端由设备类型决定：ASIO 驱动恒为 ASIO(2)，WASAPI 设备取共享(0)/独占(1)。
            int backend=d.Backend==2?2:Math.Clamp(modeBox.SelectedIndex,0,1);
            if(dsdBox.SelectedIndex==1&&dop.IsChecked!=true){args.Cancel=true;hint.Text="启用 DoP 前需要确认设备支持。";return;}
            if(dsdBox.SelectedIndex==2&&d.Backend!=2){args.Cancel=true;hint.Text="原生 DSD 需要选择 ASIO 驱动。";return;}
            if(dsdBox.SelectedIndex==2&&d.Name==AsioSetup.DriverName){args.Cancel=true;hint.Text="FlexASIO 是通用驱动，无法原生 DSD；请改用 DoP 透传（原生 DSD 需 DAC 厂商 ASIO 驱动）。";return;}
            if(dsdBox.SelectedIndex==1&&backend==0){args.Cancel=true;hint.Text="DoP 不能通过共享模式播放。";return;}
            try{var map=mapping.Text.Split(',',StringSplitOptions.TrimEntries).Select(int.Parse).Select(i=>i-1).ToArray();if(map.Length!=8||map.Any(i=>i<0)||map.Distinct().Count()!=8)throw new FormatException();
                string atId="",atName="";
                if(d.Name==AsioSetup.DriverName&&asioTarget.SelectedItem is AudioDevice t&&t.Index>=0){atId=t.Id;atName=t.Name;}
                prefs.DeviceId=d.Id;prefs.DeviceBackend=d.Backend;prefs.Profiles[$"{d.Backend}:{d.Id}"]=new(){Backend=backend,DsdMode=dsdBox.SelectedIndex,DopConfirmed=dop.IsChecked==true,ForceRate=rates[rate.SelectedIndex],Downmix=downmix.IsChecked==true,Mapping=map,AsioTargetId=atId,AsioTargetName=atName};
                prefs.Online=onlineCheck.IsOn;prefs.ReducedMotion=motion.IsOn;Ambient.Reduced=prefs.ReducedMotion;prefs.CloseToTray=closeBehavior.SelectedIndex==1;prefs.Theme=themeBox.SelectedIndex;prefs.Aurora=auroraCheck.IsOn;ApplyTheme();
                Ambient.ThemeMode=prefs.Theme==2?"light":"dark";Ambient.Aurora=prefs.Aurora&&!prefs.ReducedMotion;Ambient.ApplyAurora();
                AppPaths.Save(prefs);
                // 主题切换后按新主题重新渲染/选取氛围背景与歌词配色。
                Ambient.ThemeMode=prefs.Theme==2?"light":"dark";lyricIndex=-2;
                if(current!=null){var cover=current.Cover;lyricOffset=library.Lyrics(current.Id).Offset;await SetCover(cover);if(rawLyrics.Length>0)DisplayLyrics(rawLyrics);}
                else Ambient.SetSource(null);
            }catch{args.Cancel=true;hint.Text="请输入 8 个不同的正整数作为通道映射。";}
        };
        if(await Show(dialog)==ContentDialogResult.Primary){
            bool resume=lastState.Playing;double position=lastState.Position;
            if(current!=null&&lastState.Duration>0){
                await Play(current,position);
                if(!resume){try{audio.Pause(true);}catch(Exception ex){AppPaths.Log("设置保存后暂停: "+ex.Message);Toast("已保存","当前曲目恢复播放时请确认 DSD 播放方式");}}
            }
            Toast("输出设置已保存",Device?.Name??"");
        }
    }
    void LyricsMenu_Click(object sender,RoutedEventArgs e){var fly=new MenuFlyout();foreach(var option in new[]{"联网查找歌词","联网选择封面","导入本地 LRC","选择本地封面","调整歌词时间"}){var item=new MenuFlyoutItem{Text=option};item.Click+=async(_,_)=>{if(current==null)return;switch(option){case "联网查找歌词":await FindLyrics();break;case "联网选择封面":await FindCover();break;case "导入本地 LRC":await PickLyrics();break;case "选择本地封面":await PickCover();break;case "调整歌词时间":await AdjustLyrics();break;}};fly.Items.Add(item);}fly.ShowAt((FrameworkElement)sender);}
    async void FindLyrics_Click(object sender,RoutedEventArgs e)=>await FindLyrics();
    async Task FindLyrics(){
        if(current==null)return;
        if(!prefs.Online){Toast("联网查找已关闭","可在设置中开启。");return;}
        var t=current;var token=mediaCancellation.Token;
        var title=new TextBox{Header="歌曲名",Text=t.Title};
        var artist=new TextBox{Header="艺术家",Text=t.Artist};
        var album=new TextBox{Header="专辑",Text=t.Album};
        var status=new TextBlock{FontSize=11,Foreground=Brush(200,172,194,176),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
        var versions=new List<SongVersion>();var list=new ListBox{MaxHeight=240,FontSize=12};
        var search=new Button{Content="搜索",Padding=new Thickness(18,5,18,5)};
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};row.Children.Add(search);row.Children.Add(status);
        var panel=new StackPanel{Spacing=12,Width=470};foreach(var el in new UIElement[]{title,artist,album,row,list})panel.Children.Add(el);
        async Task DoSearch(){
            try{
                search.IsEnabled=false;status.Text="正在搜索…";list.ItemsSource=null;versions.Clear();
                versions=await online.NetEaseSongs(title.Text.Trim(),artist.Text.Trim(),album.Text.Trim(),token);
                list.ItemsSource=versions.Select(v=>v.ToString()).ToList();
                status.Text=versions.Count>0?$"找到 {versions.Count} 个版本，选中后点下方按钮":"没有找到，试试修改关键词";
            }catch(Exception ex){status.Text="搜索失败："+ex.Message;}
            finally{search.IsEnabled=true;}
        }
        search.Click+=(_,_)=>_=DoSearch();
        var dialog=Dialog("查找歌词",new ScrollViewer{Content=panel,MaxHeight=530},"使用所选版本");
        dialog.PrimaryButtonClick+=(d,args)=>{
            if(list.SelectedIndex<0||list.SelectedIndex>=versions.Count){args.Cancel=true;status.Text="请先搜索并选择一个版本。";return;}
            args.Cancel=true;var chosen=versions[list.SelectedIndex];
            _=Apply();
            async Task Apply(){
                try{
                    search.IsEnabled=false;status.Text=$"正在获取「{chosen.Name}」的歌词…";
                    var text=await online.NetEaseLyrics(chosen.Id,token);
                    if(string.IsNullOrWhiteSpace(text)){status.Text="这个版本没有歌词，换一个版本试试。";return;}
                    library.SaveLyrics(t.Id,text,lyricOffset);dialog.Hide();
                    if(current?.Id==t.Id)DisplayLyrics(text);
                    Toast("歌词已更新",chosen.Name);
                }catch(OperationCanceledException){}catch(Exception ex){status.Text="获取失败："+ex.Message;}
                finally{search.IsEnabled=true;}
            }
        };
        _=DoSearch();
        await Show(dialog);
    }
    async Task FindCover(){
        if(current==null)return;
        if(!prefs.Online){Toast("联网查找已关闭");return;}
        var t=current;var token=mediaCancellation.Token;
        var title=new TextBox{Header="歌曲名",Text=t.Title};
        var album=new TextBox{Header="专辑名",Text=t.Album};
        var artist=new TextBox{Header="艺术家",Text=t.Artist};
        var status=new TextBlock{FontSize=11,Foreground=Brush(200,172,194,176),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
        var candidates=new List<CoverCandidate>();var list=new ListView{MaxHeight=260,SelectionMode=ListViewSelectionMode.Single};
        var search=new Button{Content="搜索",Padding=new Thickness(18,5,18,5)};
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};row.Children.Add(search);row.Children.Add(status);
        var panel=new StackPanel{Spacing=12,Width=470};foreach(var el in new UIElement[]{title,album,artist,row,list})panel.Children.Add(el);
        async Task DoSearch(){
            try{
                search.IsEnabled=false;status.Text="正在搜索专辑…";list.ItemsSource=null;candidates.Clear();
                candidates=await online.Covers(album.Text.Trim(),artist.Text.Trim(),title.Text.Trim(),token);
                list.ItemsSource=candidates.Select(c=>$"{c.Title}\n{c.Artist}").ToList();
                if(candidates.Count>0)list.SelectedIndex=0;
                status.Text=candidates.Count>0?$"找到 {candidates.Count} 个版本":"没有找到，试试修改关键词";
            }catch(Exception ex){status.Text="搜索失败："+ex.Message;}
            finally{search.IsEnabled=true;}
        }
        search.Click+=(_,_)=>_=DoSearch();
        var dialog=Dialog("查找专辑封面",new ScrollViewer{Content=panel,MaxHeight=530},"使用封面");
        dialog.PrimaryButtonClick+=(d,args)=>{
            if(list.SelectedIndex<0||list.SelectedIndex>=candidates.Count){args.Cancel=true;status.Text="请先搜索并选择一个专辑。";return;}
            args.Cancel=true;_=Apply();
            async Task Apply(){
                try{
                    search.IsEnabled=false;status.Text="正在下载封面…";
                    var path=await online.DownloadCover(candidates[list.SelectedIndex],token);
                    if(path==null){status.Text="这个版本尚无封面，换一个试试。";return;}
                    t.Cover=path;library.Save(t);dialog.Hide();
                    if(current?.Id==t.Id)await SetCover(path);
                    Toast("封面已更新");
                }catch(OperationCanceledException){}catch(Exception ex){status.Text="下载失败："+ex.Message;}
                finally{search.IsEnabled=true;}
            }
        };
        _=DoSearch();
        await Show(dialog);
    }
    async Task<StorageFile?> Pick(params string[] extensions){var p=new FileOpenPicker();WinRT.Interop.InitializeWithWindow.Initialize(p,WinRT.Interop.WindowNative.GetWindowHandle(this));foreach(var ext in extensions)p.FileTypeFilter.Add(ext);return await p.PickSingleFileAsync();}
    async Task PickCover(){if(current==null)return;var t=current;var f=await Pick(".jpg",".jpeg",".png",".webp");if(f==null)return;t.Cover=f.Path;library.Save(t);if(current?.Id==t.Id)await SetCover(f.Path);}
    async Task PickLyrics(){if(current==null)return;var t=current;var f=await Pick(".lrc",".txt");if(f==null)return;var text=await File.ReadAllTextAsync(f.Path);library.SaveLyrics(t.Id,text);if(current?.Id==t.Id){lyricOffset=0;DisplayLyrics(text);}}
    async Task AdjustLyrics(){if(current==null)return;var box=new NumberBox{Header="时间偏移（秒，正数让歌词提前）",Value=lyricOffset,SmallChange=.1,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,Minimum=-60,Maximum=60};if(await Show(Dialog("歌词同步",box))==ContentDialogResult.Primary&&double.IsFinite(box.Value)){lyricOffset=box.Value;library.SaveLyrics(current.Id,rawLyrics,lyricOffset);lyricIndex=-2;}}
    void KeyDown(object sender,KeyRoutedEventArgs e){if(e.OriginalSource is TextBox or PasswordBox or Slider or ComboBox)return;if(e.Key==VirtualKey.Space){e.Handled=true;PlayPause_Click(sender,new());}else if(e.Key==VirtualKey.Escape){QueuePanel.Visibility=Visibility.Collapsed;if(nowVisible)ShowLibrary();}}
}
