namespace LumaMusic.Services;

// FlexASIO 的用户级配置（%USERPROFILE%\FlexASIO.toml）统一由本类读写。
// backend 取值必须精确匹配 PortAudio 后端名（"wasapi" 这类简写会导致驱动初始化失败）；
// device 为设备全名（字符串），由 FlexASIO 在初始化时自行解析，避免序号漂移。
public static class FlexAsioConfig
{
    public static readonly string Path=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"FlexASIO.toml");
    // 最近一次定向的结果提示，供 UI 在播放后呈现
    public static string LastWarning="";
    // 当前 toml 是否定向到了具体设备（失败重试判断用）
    public static bool Targeted;

    public static string Build(string? target)=>string.IsNullOrEmpty(target)
        ?"backend = \"Windows WASAPI\"\n\n[output]\nwasapiExclusiveMode = true\n"
        // TOML 字符串转义（设备名可能含引号/反斜杠的情况极少，防御性处理）
        :$"backend = \"Windows WASAPI\"\n\n[output]\nwasapiExclusiveMode = true\ndevice = \"{target.Replace("\\","\\\\").Replace("\"","\\\"")}\"\n";

    // 内容不变则不写，避免触发 FlexASIO 的配置热重载打断播放
    public static void Ensure(string toml)
    {
        try{if(!File.Exists(Path)||File.ReadAllText(Path)!=toml)File.WriteAllText(Path,toml);}
        catch(Exception e){AppPaths.Log("FlexAsioConfig: "+e.Message);}
    }
    public static void Reset()=>Ensure(Build(null));
}
