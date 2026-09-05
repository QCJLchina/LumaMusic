using System.Text.Json;
namespace LumaMusic.Services;
public static class AppPaths
{
    public static readonly string Root=Environment.GetEnvironmentVariable("LUMA_DATA_DIR")??System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LumaMusic");
    public static readonly string Cache=System.IO.Path.Combine(Root,"cache");
    public static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true,WriteIndented=true};
    static AppPaths(){Directory.CreateDirectory(Root);Directory.CreateDirectory(Cache);}
    public static Preferences Load(){try{return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(System.IO.Path.Combine(Root,"settings.json")),Json)??new();}catch{return new();}}
    public static void Save(Preferences prefs){try{var file=System.IO.Path.Combine(Root,"settings.json");File.WriteAllText(file+".tmp",JsonSerializer.Serialize(prefs,Json));File.Move(file+".tmp",file,true);}catch(Exception e){Log(e.Message);}}
    public static void Log(string text){try{File.AppendAllText(System.IO.Path.Combine(Root,"luma.log"),$"{DateTime.Now:O} {text}\n");}catch{}}
}
