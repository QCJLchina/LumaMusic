using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
namespace LumaMusic;

public class Track : INotifyPropertyChanged
{
    public string Id {get;set;}="";
    public string Path {get;set;}="";
    public int Subsong {get;set;}=1;
    public string Title {get;set;}="";
    public string Artist {get;set;}="未知艺术家";
    public string Album {get;set;}="未分类专辑";
    public double Duration {get;set;}
    public int SampleRate {get;set;}
    public int Channels {get;set;}=2;
    public int Bits {get;set;}
    public string Format {get;set;}="";
    public long Modified {get;set;}
    string cover=""; public string Cover {get=>cover;set{cover=value;OnChanged();}}
    // x:Bind 函数直接返回 ImageSource，绕开 XamlBindingHelper.ConvertValue——
    // 它对空字符串（甚至 null）都会抛 ArgumentException，且未处理异常会让整个应用崩溃。
    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? CoverImage(string cover)=>string.IsNullOrEmpty(cover)?null:new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(cover));
    bool favorite;public bool Favorite {get=>favorite;set{favorite=value;OnChanged();OnChanged(nameof(FavoriteGlyph));}}
    public string FavoriteGlyph=>Favorite?"\uEB52":"\uEB51";
    public string DurationText=>TimeSpan.FromSeconds(Math.Max(Duration,0)).ToString(@"m\:ss");
    public string Quality=>Format is "DSF" or "DFF" or "ISO"?$"DSD{SampleRate/44100} · {Channels}ch":$"{Format} · {SampleRate/1000d:0.#} kHz";
    public string Subtitle=>$"{Artist} · {Album}";
    public string AlbumKey=>$"{Album}\n{Artist}";
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
public record AudioDevice(int Index,int Backend,string Name,string Id,bool Default,int Channels,int Rate)
{
    public override string ToString()=>Name+(Backend==2?"  ·  ASIO":"  ·  WASAPI");
}
public class PlaybackState
{
    public bool Playing {get;set;} public bool Ended {get;set;} public bool Failed {get;set;}
    public string Error {get;set;}=""; public double Position {get;set;} public double Duration {get;set;}
    public int SourceRate {get;set;} public int SourceChannels {get;set;} public int Rate {get;set;} public int Channels {get;set;}
    public int Bits {get;set;} public bool Dsd {get;set;} public int Mode {get;set;} public int Backend {get;set;} public long Underruns {get;set;}
    public string SourceLabel=>Dsd?$"DSD{SourceRate/44100}":$"PCM {SourceRate/1000d:0.#} kHz";
    public string OutputLabel=>Dsd&&Mode==2?$"Native DSD · {Rate/1000000d:0.###} MHz":Dsd&&Mode==1?$"DoP · {Rate/1000d:0.#} kHz":$"PCM · {Rate/1000d:0.#} kHz";
    public string BackendLabel=>Backend==2?"ASIO":Backend==1?"WASAPI 独占":"WASAPI 共享";
    public string Chain=>$"{SourceLabel}  →  {OutputLabel}  ·  {Channels}ch  ·  {BackendLabel}";
}
public class DeviceProfile
{
    public int Backend {get;set;}=1;public int DsdMode {get;set;}=3;public bool DopConfirmed {get;set;}
    public int PcmRate {get;set;}=176400;public int ForceRate {get;set;} public bool Downmix {get;set;}
    public int[] Mapping {get;set;}=[0,1,2,3,4,5,6,7];
    // FlexASIO 定向目标（端点 ID 为身份，名字给 FlexASIO 匹配用）；空 = Windows 默认输出
    public string AsioTargetId {get;set;}="";public string AsioTargetName {get;set;}="";
}
public class Preferences
{
    public string DeviceId {get;set;}="";public int DeviceBackend {get;set;}=1;
    public float Volume {get;set;}=.8f;public bool Online {get;set;}=true;public bool ReducedMotion {get;set;}
    public bool Shuffle {get;set;} public int Repeat {get;set;}
    public Dictionary<string,DeviceProfile> Profiles {get;set;}=[];
    public List<string> Roots {get;set;}=[];public List<string> Queue {get;set;}=[];
    public string LastTrack {get;set;}="";public double LastPosition {get;set;}
    // 主题：0 跟随系统 / 1 深色 / 2 浅色；关闭窗口最小化到托盘；背景流光（取色自封面）
    public int Theme {get;set;} public bool CloseToTray {get;set;} public bool Aurora {get;set;}=true;
    // 歌曲列表点击行为：false 单击播放（默认），true 双击播放
    public bool DoubleClickPlay {get;set;}
    public bool ReducedTransparency {get;set;}
    // 0 自动，1 轻量，2 完整；旧配置缺失时按自动处理。
    public int VisualQuality {get;set;}
    public DateTime LastUpdateCheck {get;set;}
    public DeviceProfile Profile=>Profiles.TryGetValue($"{DeviceBackend}:{DeviceId}",out var p)?p:Profiles[$"{DeviceBackend}:{DeviceId}"]=new(){Backend=DeviceBackend};
}
public record LyricLine(double Time,string Text);
public record Playlist(long Id,string Name);
public record ArtistSummary(string Name, int Albums, int Songs)
{
    public string Description => $"{Albums} 张专辑 · {Songs} 首歌曲";
}
public record CoverCandidate(string Title,string Artist,string ReleaseId,string Thumbnail,string Source="");
public record SongVersion(string Id,string Name,string Artist,string Album,string Source="",double Duration=0,bool Synced=true)
{
    public override string ToString()=>string.IsNullOrEmpty(Source)?$"{Name}  /  {Artist}  ·  {Album}":$"[{Source}] {Name}  /  {Artist}  ·  {Album}";
}
