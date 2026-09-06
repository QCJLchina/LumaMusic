using System.Diagnostics;
namespace LumaMusic.Services;

// 随应用分发的 FlexASIO 通用 ASIO 驱动安装器（GPLv3，见 THIRD-PARTY-NOTICES.md）。
// 安装后写入用户级独占配置，使其成为"WASAPI 独占的 ASIO 接口"——开箱即得的独占语义。
public static class AsioSetup
{
    public const string DriverName="FlexASIO";
    static string SetupPath=>Path.Combine(AppContext.BaseDirectory,"FlexASIOSetup.exe");
    static string ConfigPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"FlexASIO.toml");
    // backend 取值必须精确匹配 PortAudio 后端名（小写 "wasapi" 无效，会导致驱动初始化失败）
    // 采样率不锁死，由播放引擎按音源通过 ASIO 设置
    const string ExclusiveConfig="backend = \"Windows WASAPI\"\nwasapiExclusiveMode = true\n";

    public static async Task<(bool ok,string error)> InstallAsync()
    {
        try{
            if(!File.Exists(SetupPath))return(false,"应用目录缺少 FlexASIOSetup.exe，请从 GitHub Releases 重新下载完整包。");
            using var p=Process.Start(new ProcessStartInfo(SetupPath){Arguments="/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",UseShellExecute=true,Verb="runas"});
            if(p==null)return(false,"无法启动驱动安装程序。");
            await p.WaitForExitAsync();
            if(p.ExitCode!=0)return(false,$"驱动安装未完成（退出码 {p.ExitCode}），可能取消了管理员授权。");
            if(!File.Exists(ConfigPath))await File.WriteAllTextAsync(ConfigPath,ExclusiveConfig);
            return(true,"");
        }catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode==1223){
            return(false,"已取消管理员授权，驱动未安装。");
        }catch(Exception e){
            AppPaths.Log("AsioSetup: "+e.Message);
            return(false,"安装失败："+e.Message);
        }
    }
}
