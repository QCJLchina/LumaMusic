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

    // dop=true 时强制 24 位容器：FlexASIO 默认按设备默认格式打开（可能是 Int32），DoP-32 部分 DAC 不认。
    // 独占模式下 WASAPI 从不转换采样率，rate 由播放引擎经 ASIO 正常驱动，故不锁 sampleRate。
    public static string Build(string? target,bool dop=false)
    {
        string output=dop
            ?"[output]\nwasapiExclusiveMode = true\nsampleType = \"Int24\"\n"
            :"[output]\nwasapiExclusiveMode = true\n";
        string head="backend = \"Windows WASAPI\"\n\n"+output;
        // TOML 字符串转义（设备名可能含引号/反斜杠的情况极少，防御性处理）
        return string.IsNullOrEmpty(target)?head:head+$"device = \"{target.Replace("\\","\\\\").Replace("\"","\\\"")}\"\n";
    }

    // 内容不变则不写，避免触发 FlexASIO 的配置热重载打断播放
    public static void Ensure(string toml)
    {
        try{if(!File.Exists(Path)||File.ReadAllText(Path)!=toml)File.WriteAllText(Path,toml);}
        catch(Exception e){AppPaths.Log("FlexAsioConfig: "+e.Message);}
    }
    public static void Reset()=>Ensure(Build(null));
}
