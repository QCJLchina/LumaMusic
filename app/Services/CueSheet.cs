using System.Globalization;
using System.Text;
namespace LumaMusic.Services;

/// CUE sheet 解析：纯文本→结构，不碰磁盘，方便直接回归测试。
public static class CueSheet
{
    public record Entry(int Number,double Start,string Title,string Artist);
    public record Sheet(string AudioFile,string Album,string AlbumArtist,string Genre,string Year,List<Entry> Entries);

    // 中文 cue 极常见 GBK：严格 UTF-8 解码失败时整体回退，绝不让导入中断。
    static string Decode(byte[] bytes)
    {
        if(bytes.Length>=3&&bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF)return Encoding.UTF8.GetString(bytes,3,bytes.Length-3);
        try{
            // 严格解码不抛不代表是 UTF-8：GBK 中文几乎必然出现 U+FFFD，据此再回退。
            var text=new UTF8Encoding(false,true).GetString(bytes);
            return text.Contains('\uFFFD')?Fallback(bytes):text;
        }catch{return Fallback(bytes);}
    }
    static string Fallback(byte[] bytes)
    {
        try{
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }catch{return Encoding.UTF8.GetString(bytes);}
    }

    // INDEX 时间 mmm:ss:ff，ff 是 1/75 秒帧；容忍 mm:ss 两位形式与 ff 缺失。
    static double Seconds(string time)
    {
        var parts=time.Split(':');
        if(parts.Length<2)return 0;
        if(!int.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out var m))return 0;
        if(!int.TryParse(parts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out var s))return 0;
        double total=m*60+s;
        if(parts.Length>=3&&int.TryParse(parts[2],NumberStyles.Integer,CultureInfo.InvariantCulture,out var f))total+=f/75.0;
        return total;
    }
    static string Unquote(string value)
    {
        value=value.Trim();
        if(value.Length>=2&&value[0]=='"'&&value[^1]=='"')value=value[1..^1];
        return value.Trim();
    }
    // FILE 行是 FILE "整轨.wav" WAVE：引号只包住文件名，后面还跟着类型 token。
    // 逐 token 扫描找第一个带引号的更稳，也兼容 FILE 整轨.wav（无引号）。
    static string ParseFileName(string rest)
    {
        rest=rest.Trim();
        if(rest.StartsWith('"'))
        {
            var close=rest.IndexOf('"',1);
            if(close>1)return rest[1..close];
        }
        var space=rest.IndexOf(' ');
        var name=space<0?rest:rest[..space];
        return System.IO.Path.GetFileName(name.Trim());
    }

    /// <returns>解析失败或没有有效 INDEX 01 时返回 null，调用方据此回退整轨单曲。</returns>
    public static Sheet? Parse(string text)
    {
        string audio="",album="",albumArtist="",genre="",year="";
        int number=0;double start=-1;string title="",artist="";
        var entries=new List<Entry>();
        void Flush()
        {
            // 必须有 INDEX 01 才是一首真正的曲目；只有 INDEX 00 的段是前奏，丢弃。
            if(number>0&&start>=0)entries.Add(new(number,start,title,artist));
            number=0;start=-1;title="";artist="";
        }
        foreach(var raw in text.Replace("\r","").Split('\n')){
            var line=raw.Trim();if(line.Length==0)continue;
            var space=line.IndexOf(' ');
            var command=(space<0?line:line[..space]).ToUpperInvariant();
            var rest=space<0?"":line[(space+1)..].Trim();
            switch(command){
                case "FILE":if(audio.Length==0)audio=ParseFileName(rest);break;
                case "TRACK":
                    Flush();
                    if(int.TryParse(rest.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).FirstOrDefault(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed))number=parsed;
                    break;
                case "TITLE":title=Unquote(rest);break;
                case "PERFORMER":artist=Unquote(rest);break;
                case "SONGWRITER":break;
                case "REM":
                    var rem=rest.Split(' ',2,StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
                    if(rem.Length<2)break;
                    var value=Unquote(rem[1]);
                    switch(rem[0].ToUpperInvariant()){
                        case "ALBUM":album=value;break;
                        case "ALBUMARTIST":albumArtist=value;break;
                        case "GENRE":genre=value;break;
                        case "DATE":year=value;break;
                    }
                    break;
                default:
                    // INDEX 跟在 TRACK 之后，只有序号 01 才是首帧（00 是前奏）。
                    if(number>0&&command.StartsWith("INDEX",StringComparison.Ordinal)){
                        var fields=rest.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
                        if(fields.Length>=2&&int.TryParse(fields[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out var idx)&&idx==1)start=Seconds(fields[1]);
                    }
                    break;
            }
        }
        Flush();
        if(entries.Count==0||audio.Length==0)return null;
        // 少数 cue 的 INDEX 顺序乱排，按时间归位；Number 保持原值作为展示用序号。
        entries.Sort((a,b)=>a.Start.CompareTo(b.Start));
        return new(audio,album,albumArtist,genre,year,entries);
    }

    /// 读取音频文件同目录同名 .cue；FILE 不指向该音频（同一张盘多整轨）时返回 null，
    /// 避免同一首歌被多个 cue 重复导入。
    public static Sheet? TryLoad(string audioPath)
    {
        try{
            var cue=System.IO.Path.ChangeExtension(audioPath,".cue");
            if(!File.Exists(cue))return null;
            var sheet=Parse(Decode(File.ReadAllBytes(cue)));
            if(sheet==null)return null;
            if(!string.Equals(sheet.AudioFile,System.IO.Path.GetFileName(audioPath),StringComparison.OrdinalIgnoreCase))return null;
            return sheet;
        }catch{return null;}
    }
}