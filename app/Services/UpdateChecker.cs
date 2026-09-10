using System.Net.Http;
using System.Reflection;
using System.Text.Json;
namespace LumaMusic.Services;
// GitHub Releases 更新检查：MSIX 与 NSIS 安装版都可应用内下载覆盖安装，便携版跳转下载页。
public static class UpdateChecker
{
    public const string Repo="QCJLchina/LumaMusic";
    // NSIS 安装器在自己解包目录里留的标记（packaging/LumaMusic.nsi 的 MARKER）：
    // 既用来识别"这个副本是安装版"，也是安装器覆盖安装前敢不敢清空目录的依据，两边必须一致。
    public const string InstallMarker=".luma-nsis-install";
    static readonly HttpClient client=new(){Timeout=TimeSpan.FromSeconds(12)};
    public static string CurrentVersion=>Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]??"1.1.2";
    public static bool IsPackaged
    {
        get{
            try{return Windows.ApplicationModel.Package.Current.Id.FamilyName.Length>0;}
            catch{return false;}
        }
    }
    // 三种发行形态的更新方式不同：MSIX 走 Windows 安装器，NSIS 走安装器静默覆盖，其余按便携版处理。
    public enum UpdateMode{Portable,Msix,Installer}
    public static UpdateMode Mode=>IsPackaged?UpdateMode.Msix
        :File.Exists(Path.Combine(AppContext.BaseDirectory,InstallMarker))?UpdateMode.Installer
        :UpdateMode.Portable;
    public record UpdateInfo(string Tag,string Url,string Notes);
    public static bool IsNewer(string tag)=>Version.TryParse(tag.TrimStart('v','V').Split('-')[0],out var v)&&v>Version.Parse(CurrentVersion);
    public static async Task<UpdateInfo?> Latest(CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"LumaMusic/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response=await client.SendAsync(request,token);
        if(!response.IsSuccessStatusCode)return null;
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var r=doc.RootElement;
        var tag=r.GetProperty("tag_name").GetString()??"";
        var url=r.TryGetProperty("html_url",out var u)?u.GetString()??$"https://github.com/{Repo}/releases":"";
        string notes="";
        if(r.TryGetProperty("body",out var b)&&b.ValueKind==JsonValueKind.String)notes=b.GetString()??"";
        return new UpdateInfo(tag,url,notes.Length>400?notes[..400]+"…":notes);
    }
    // 便携版没有安装器语境，交给浏览器；其余把对应安装包下载到临时目录，交由安装器接管。
    public static Task<string?> DownloadPackage(string tag,CancellationToken token)
        =>DownloadAsset(tag,n=>n.EndsWith(".msix",StringComparison.OrdinalIgnoreCase),token);
    // 发布资产里有 .zip/.msix/.cer/.exe，按后缀取会误命中，所以由调用方给精确的匹配条件。
    public static async Task<string?> DownloadAsset(string tag,Func<string,bool> match,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/{Repo}/releases/tags/{Uri.EscapeDataString(tag)}");
        request.Headers.UserAgent.ParseAdd($"LumaMusic/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response=await client.SendAsync(request,token);
        if(!response.IsSuccessStatusCode)return null;
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        string? assetUrl=null,assetName=null;
        foreach(var a in doc.RootElement.GetProperty("assets").EnumerateArray()){
            var name=a.GetProperty("name").GetString()??"";var url=a.GetProperty("browser_download_url").GetString()??"";
            if(match(name)){assetUrl=url;assetName=name;break;}
        }
        if(assetUrl==null)return null;
        var temp=Path.Combine(Path.GetTempPath(),assetName!);
        using var download=await client.GetAsync(assetUrl,HttpCompletionOption.ResponseHeadersRead,token);
        download.EnsureSuccessStatusCode();
        await using var source=await download.Content.ReadAsStreamAsync(token);
        await using var target=File.Create(temp);
        await source.CopyToAsync(target,token);
        return temp;
    }
}
