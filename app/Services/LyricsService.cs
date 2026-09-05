using System.Globalization;
using System.Text.RegularExpressions;
namespace LumaMusic.Services;
public static partial class LyricsService
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"\[offset:([+-]?\d+)\]",RegexOptions.IgnoreCase)]
    private static partial Regex Offset();
    public static List<LyricLine> Parse(string text)
    {
        var list=new List<LyricLine>();var offsetMatch=Offset().Match(text);
        double offset=offsetMatch.Success?double.Parse(offsetMatch.Groups[1].Value,CultureInfo.InvariantCulture)/1000:0;
        foreach(var raw in text.Replace("\r","").Split('\n')){
            var matches=Timestamp().Matches(raw);string line=Timestamp().Replace(raw,"").Trim();
            foreach(Match match in matches){double fraction=match.Groups[3].Success?double.Parse("0."+match.Groups[3].Value,CultureInfo.InvariantCulture):0;double time=int.Parse(match.Groups[1].Value)*60+int.Parse(match.Groups[2].Value)+fraction+offset;list.Add(new(Math.Max(0,time),line));}
        }
        return list.OrderBy(l=>l.Time).ToList();
    }
    public static async Task<string> Local(Track track)
    {
        var lrc=System.IO.Path.ChangeExtension(track.Path,".lrc");
        if(File.Exists(lrc))return await File.ReadAllTextAsync(lrc);
        try{using var f=TagLib.File.Create(track.Path);return f.Tag.Lyrics??"";}catch{return "";}
    }
    public static int Current(IReadOnlyList<LyricLine> lines,double position)
    {
        int low=0,high=lines.Count-1,result=-1;
        while(low<=high){int mid=(low+high)/2;if(lines[mid].Time<=position){result=mid;low=mid+1;}else high=mid-1;}return result;
    }
}
