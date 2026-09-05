using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace LumaMusic.Services;
public sealed class OnlineService : IDisposable
{
    readonly HttpClient client=new(){Timeout=TimeSpan.FromSeconds(15)};
    readonly SemaphoreSlim gate=new(1,1);
    DateTime nextRequest=DateTime.MinValue;
    public OnlineService(){client.DefaultRequestHeaders.UserAgent.ParseAdd("LumaMusic/0.1 (Windows; personal desktop player)");}
    async Task<byte[]?> Get(string url,int maxBytes,CancellationToken token,bool netEase=false)
    {
        await gate.WaitAsync(token);
        try{
            var delay=nextRequest-DateTime.UtcNow;if(delay>TimeSpan.FromSeconds(10))throw new HttpRequestException("服务请求过于频繁，请稍后重试");if(delay>TimeSpan.Zero)await Task.Delay(delay,token);
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            if(netEase){request.Headers.Referrer=new Uri("https://music.163.com/");request.Headers.UserAgent.ParseAdd("Mozilla/5.0");}
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);nextRequest=DateTime.UtcNow.AddMilliseconds(1100);
            if(response.StatusCode==HttpStatusCode.NotFound)return null;
            if((int)response.StatusCode==429){nextRequest=DateTime.UtcNow.Add(response.Headers.RetryAfter?.Delta??TimeSpan.FromMinutes(1));throw new HttpRequestException("在线服务限流，请稍后重试");}
            response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>maxBytes)throw new IOException("在线资源超过大小限制");
            using var data=new MemoryStream();using var stream=await response.Content.ReadAsStreamAsync(token);var buffer=new byte[16384];int n;while((n=await stream.ReadAsync(buffer,token))>0){if(data.Length+n>maxBytes)throw new IOException("在线资源过大");data.Write(buffer,0,n);}return data.ToArray();
        }finally{gate.Release();}
    }
    public Task<string?> Lyrics(Track t,CancellationToken token)=>Lyrics(t.Title,t.Artist,t.Album,t.Duration,token);
    public async Task<string?> Lyrics(string title,string artist,string album,double duration,CancellationToken token)
    {
        var songs=await NetEaseSongs(title,artist,album,token);
        // 自动路径只信任高匹配度的版本，拿不准就交给 lrclib 或手动查找。
        var best=songs.FirstOrDefault(s=>Score(s,title,artist,album)>=4);
        if(best!=null){var text=await NetEaseLyrics(best.Id,token);if(!string.IsNullOrWhiteSpace(text))return text;}
        var url=$"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}&album_name={Uri.EscapeDataString(album)}&duration={duration.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)}";
        var bytes=await Get(url,2*1024*1024,token);if(bytes==null)return null;
        using var doc=JsonDocument.Parse(bytes);var r=doc.RootElement;
        if(r.TryGetProperty("instrumental",out var instrumental)&&instrumental.ValueKind==JsonValueKind.True)return "[纯音乐]";
        return r.TryGetProperty("syncedLyrics",out var synced)&&synced.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(synced.GetString())?synced.GetString():r.TryGetProperty("plainLyrics",out var plain)?plain.GetString():null;
    }
    static string Strip(string s)=>Regex.Replace(s,@"[\(\[（【].*?[\)\]）】]","").Trim();
    static int Score(SongVersion s,string title,string artist,string album)
    {
        int score=0;
        var name=Strip(s.Name);var target=Strip(title);
        if(name.Equals(target,StringComparison.OrdinalIgnoreCase))score+=4;else if(name.Contains(target,StringComparison.OrdinalIgnoreCase)||target.Contains(name,StringComparison.OrdinalIgnoreCase))score+=1;
        var artistName=Strip(s.Artist);var targetArtist=Strip(artist);
        if(artistName.Equals(targetArtist,StringComparison.OrdinalIgnoreCase))score+=4;else if(artistName.Contains(targetArtist,StringComparison.OrdinalIgnoreCase)||targetArtist.Contains(artistName,StringComparison.OrdinalIgnoreCase))score+=1;
        if(Strip(s.Album).Equals(Strip(album),StringComparison.OrdinalIgnoreCase))score+=2;
        foreach(var bad in new[]{"翻自","卡拉OK","karaoke","cover","live","现场"})if(s.Name.Contains(bad,StringComparison.OrdinalIgnoreCase))score-=3;
        return score;
    }
    static string FirstArtist(JsonElement song)
    {
        if(!song.TryGetProperty("artists",out var artists)||artists.ValueKind!=JsonValueKind.Array||artists.GetArrayLength()==0)return "";
        var first=artists[0];return first.TryGetProperty("name",out var name)?name.GetString()??"":"";
    }
    // 网易云按 歌名+歌手 搜歌，返回按匹配度排序的全部候选，供自动匹配与手动选择共用。
    public async Task<List<SongVersion>> NetEaseSongs(string title,string artist,string album,CancellationToken token)
    {
        try{
            var query=string.Join(" ",new[]{title,artist}.Where(x=>!string.IsNullOrWhiteSpace(x)));
            if(query.Length==0)return [];
            var bytes=await Get($"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&limit=20",2*1024*1024,token,netEase:true);
            if(bytes==null)return [];using var doc=JsonDocument.Parse(bytes);
            if(!doc.RootElement.TryGetProperty("result",out var result)||!result.TryGetProperty("songs",out var songs))return [];
            var list=new List<SongVersion>();
            foreach(var s in songs.EnumerateArray()){
                var name=s.GetProperty("name").GetString()??"";
                // 伴奏/铃声/卡拉OK版本永远没有我们要的歌词，直接跳过。
                if(new[]{"伴奏","铃声","inst.","卡拉OK","カラオケ","instrumental"}.Any(bad=>name.Contains(bad,StringComparison.OrdinalIgnoreCase)))continue;
                var version=new SongVersion(s.GetProperty("id").GetInt64(),name,FirstArtist(s),s.TryGetProperty("album",out var al)?al.GetProperty("name").GetString()??"":"");
                list.Add(version);
            }
            return list.OrderByDescending(v=>Score(v,title,artist,album)).ToList();
        }catch(Exception){return [];}
    }
    public async Task<string?> NetEaseLyrics(long id,CancellationToken token)
    {
        var bytes=await Get($"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1",2*1024*1024,token,netEase:true);
        if(bytes==null)return null;using var doc=JsonDocument.Parse(bytes);var r=doc.RootElement;
        string lrc=r.TryGetProperty("lrc",out var l)&&l.TryGetProperty("lyric",out var ly)?ly.GetString()??"" :"";
        string trans=r.TryGetProperty("tlyric",out var tl)&&tl.TryGetProperty("lyric",out var ty)?ty.GetString()??"" :"";
        return MergeTranslation(lrc,trans);
    }
    // 把网易云的中文翻译按时间轴合并进原文，显示为「原文 / 译文」双语行。
    internal static string? MergeTranslation(string lrc,string tlyric)
    {
        if(string.IsNullOrWhiteSpace(lrc))return null;
        if(string.IsNullOrWhiteSpace(tlyric))return lrc;
        var trans=LyricsService.Parse(tlyric).Where(x=>x.Text.Length>0).ToLookup(x=>(long)Math.Round(x.Time*1000),x=>x.Text);
        if(!trans.Any())return lrc;
        static string Fmt(double time){var ts=TimeSpan.FromSeconds(time);return $"{ts.Minutes:d2}:{ts.Seconds:d2}.{ts.Milliseconds:d3}";}
        var merged=new StringBuilder();
        foreach(var line in LyricsService.Parse(lrc)){
            string? tr=null;
            if(line.Text.Length>0){
                var key=(long)Math.Round(line.Time*1000);
                for(int d=0;d<=50&&tr==null;d+=25){
                    if(trans.Contains(key+d))tr=trans[key+d].First();
                    else if(d>0&&trans.Contains(key-d))tr=trans[key-d].First();
                }
            }
            var text=string.IsNullOrEmpty(tr)?line.Text:$"{line.Text}  /  {tr}";
            merged.AppendLine($"[{Fmt(line.Time)}]{text}");
        }
        return merged.ToString();
    }
    public Task<List<CoverCandidate>> Covers(Track t,CancellationToken token)=>Covers(t.Album,t.Artist,t.Title,token);
    public async Task<List<CoverCandidate>> Covers(string album,string artist,string title,CancellationToken token)
    {
        var netEase=await NetEaseCovers(album,artist,title,token);if(netEase.Count>0)return netEase;
        static string Clean(string s)=>s.Replace("\\","").Replace("\"","");
        var query=$"release:\"{Clean(album)}\" AND artist:\"{Clean(artist)}\"";
        var bytes=await Get("https://musicbrainz.org/ws/2/release/?fmt=json&limit=8&query="+Uri.EscapeDataString(query),2*1024*1024,token);
        if(bytes==null)return [];
        using var doc=JsonDocument.Parse(bytes);var list=new List<CoverCandidate>();
        foreach(var r in doc.RootElement.GetProperty("releases").EnumerateArray()){
            var id=r.GetProperty("id").GetString()!;if(!Guid.TryParse(id,out _))continue;
            string artistCredit=r.TryGetProperty("artist-credit",out var credits)?string.Join(" / ",credits.EnumerateArray().Where(a=>a.TryGetProperty("name",out _)).Select(a=>a.GetProperty("name").GetString())):"";
            list.Add(new(r.GetProperty("title").GetString()??"",artistCredit,id,$"https://coverartarchive.org/release/{id}/front-500"));
        }return list;
    }
    async Task<List<CoverCandidate>> NetEaseCovers(string album,string artist,string title,CancellationToken token)
    {
        try{
            // 依次尝试 专辑+歌手、专辑、歌名+歌手，返回第一组有相关结果的候选。
            var queries=new[]{
                string.Join(" ",new[]{album,artist}.Where(x=>!string.IsNullOrWhiteSpace(x))),
                album??"",
                string.Join(" ",new[]{title,artist}.Where(x=>!string.IsNullOrWhiteSpace(x))),
            }.Where(q=>q.Trim().Length>0).Distinct().ToArray();
            foreach(var query in queries){
                var bytes=await Get($"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=10&limit=8",2*1024*1024,token,netEase:true);
                if(bytes==null)continue;using var doc=JsonDocument.Parse(bytes);
                var list=new List<CoverCandidate>();
                if(doc.RootElement.TryGetProperty("result",out var result)&&result.TryGetProperty("albums",out var albums))
                    foreach(var a in albums.EnumerateArray()){
                        long id=a.GetProperty("id").GetInt64();
                        var pic=(a.TryGetProperty("picUrl",out var p)?p.GetString():null)??"";
                        if(id<=0||pic.Length==0)continue;
                        if(pic.StartsWith("http://",StringComparison.OrdinalIgnoreCase))pic="https://"+pic[7..];
                        pic+=pic.Contains('?')?"&param=800y800":"?param=800y800";
                        var artistName=a.TryGetProperty("artist",out var ar)&&ar.TryGetProperty("name",out var an)?an.GetString()??"":"";
                        var candidate=new CoverCandidate(a.GetProperty("name").GetString()??"",artistName,$"ne{id}",pic);
                        if(!list.Any(c=>c.ReleaseId==candidate.ReleaseId))list.Add(candidate);
                    }
                // 网易云长查询的相关性很差，会混入毫不相干的专辑；只保留和查询词有交集的结果。
                var terms=query.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Where(x=>x.Length>=2).ToArray();
                var relevant=list.Where(c=>terms.Any(term=>c.Title.Contains(term,StringComparison.OrdinalIgnoreCase)||c.Artist.Contains(term,StringComparison.OrdinalIgnoreCase))).ToList();
                if(relevant.Count>0)return relevant;
            }
            return [];
        }catch(Exception){return [];}
    }
    public async Task<string?> DownloadCover(CoverCandidate candidate,CancellationToken token)
    {
        var file=System.IO.Path.Combine(AppPaths.Cache,"cover-"+candidate.ReleaseId+".img");if(File.Exists(file))return file;
        var data=await Get(candidate.Thumbnail,12*1024*1024,token);if(data==null)return null;
        // Decode before accepting a server response as cover art.
        using var stream=new Windows.Storage.Streams.InMemoryRandomAccessStream();using(var writer=new Windows.Storage.Streams.DataWriter(stream)){writer.WriteBytes(data);await writer.StoreAsync();writer.DetachStream();}stream.Seek(0);
        var decoder=await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);if(decoder.PixelWidth>16000||decoder.PixelHeight>16000)throw new IOException("封面尺寸过大");
        await File.WriteAllBytesAsync(file,data,token);return file;
    }
    public void Dispose(){client.Dispose();gate.Dispose();}
}
