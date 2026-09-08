using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
        var songs=await SearchSongs(title,artist,album,token);
        // 自动路径只信任高匹配度的版本，拿不准就交给 lrclib 或手动查找。
        var best=songs.Where(s=>s.Duration<=0||duration<=0||Math.Abs(s.Duration-duration)<=Math.Max(3,duration*.02)).OrderByDescending(s=>Score(s,title,artist,album)).FirstOrDefault(s=>Score(s,title,artist,album)>=4);
        if(best!=null){var text=await LyricsFor(best,token);if(!string.IsNullOrWhiteSpace(text))return text;}
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
                var version=new SongVersion(s.GetProperty("id").GetInt64().ToString(),name,FirstArtist(s),s.TryGetProperty("album",out var al)?al.GetProperty("name").GetString()??"":"","网易云",s.TryGetProperty("dt",out var dt)?dt.GetDouble()/1000:0);
                list.Add(version);
            }
            return list.OrderByDescending(v=>Score(v,title,artist,album)).ToList();
        }catch(Exception){return [];}
    }
    public async Task<string?> NetEaseLyrics(string id,CancellationToken token)
    {
        var bytes=await Get($"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1",2*1024*1024,token,netEase:true);
        if(bytes==null)return null;using var doc=JsonDocument.Parse(bytes);var r=doc.RootElement;
        string lrc=r.TryGetProperty("lrc",out var l)&&l.TryGetProperty("lyric",out var ly)?ly.GetString()??"" :"";
        string trans=r.TryGetProperty("tlyric",out var tl)&&tl.TryGetProperty("lyric",out var ty)?ty.GetString()??"" :"";
        return MergeTranslation(lrc,trans);
    }
    public async Task<List<SongVersion>> SearchSongs(string title,string artist,string album,CancellationToken token)
    {
        var jobs=new[]{SafeSongs(() => NetEaseSongs(title,artist,album,token)),SafeSongs(() => QqSongs(title,artist,album,token)),SafeSongs(() => KugouSongs(title,artist,album,token))};
        var result=(await Task.WhenAll(jobs)).SelectMany(x=>x).GroupBy(x=>$"{x.Source}:{x.Id}").Select(g=>g.First()).ToList();
        return result.OrderByDescending(v=>Score(v,title,artist,album)).ToList();
    }
    static async Task<List<SongVersion>> SafeSongs(Func<Task<List<SongVersion>>> work){try{return await work();}catch{return [];}}
    static async Task<List<CoverCandidate>> SafeCovers(Func<Task<List<CoverCandidate>>> work){try{return await work();}catch{return [];}}
    async Task<List<SongVersion>> QqSongs(string title,string artist,string album,CancellationToken token)
    {
        var query=string.Join(" ",new[]{title,artist}.Where(x=>!string.IsNullOrWhiteSpace(x)));if(query.Length==0)return [];
        var payload=new Dictionary<string,object>{{"music.search.SearchCgiService",new Dictionary<string,object>{{"method","DoSearchForQQMusicDesktop"},{"module","music.search.SearchCgiService"},{"param",new Dictionary<string,object>{{"num_per_page",20},{"page_num",1},{"query",query},{"search_type",0}}}}}};
        using var response=await client.PostAsync("https://u.y.qq.com/cgi-bin/musicu.fcg",JsonContent.Create(payload),token);if(!response.IsSuccessStatusCode)return [];
        using var doc=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));var service=doc.RootElement.GetProperty("music.search.SearchCgiService");var songs=service.GetProperty("data").GetProperty("body").GetProperty("song").GetProperty("list");var list=new List<SongVersion>();
        foreach(var s in songs.EnumerateArray()){
            var singers=s.TryGetProperty("singer",out var ss)?string.Join(" / ",ss.EnumerateArray().Select(x=>x.GetProperty("name").GetString())):"";var al=s.TryGetProperty("album",out var a)?a.GetProperty("name").GetString()??"":"";var mid=s.GetProperty("mid").GetString()??"";if(mid.Length>0)list.Add(new(mid,s.GetProperty("title").GetString()??"",singers,al,"QQ音乐",s.TryGetProperty("interval",out var d)?d.GetDouble():0));
        }return list;
    }
    async Task<List<SongVersion>> KugouSongs(string title,string artist,string album,CancellationToken token)
    {
        var query=Uri.EscapeDataString(string.Join(" ",new[]{title,artist}.Where(x=>!string.IsNullOrWhiteSpace(x))));if(query.Length==0)return [];
        var bytes=await Get($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={query}&duration=-1",2*1024*1024,token);if(bytes==null)return [];using var doc=JsonDocument.Parse(bytes);if(!doc.RootElement.TryGetProperty("candidates",out var cs))return [];var list=new List<SongVersion>();
        foreach(var c in cs.EnumerateArray()){var name=c.TryGetProperty("song",out var n)?n.GetString()??"":"";var singer=c.TryGetProperty("singer",out var a)?a.GetString()??"":"";var id=c.TryGetProperty("id",out var i)?i.GetString()??"":"";if(id.Length>0)list.Add(new($"{id}|{c.GetProperty("accesskey").GetString()}",name,singer,album,"酷狗",c.TryGetProperty("duration",out var d)?d.GetDouble()/1000:0));}return list;
    }
    public Task<string?> Lyrics(SongVersion song,CancellationToken token)=>LyricsFor(song,token);
    async Task<string?> LyricsFor(SongVersion song,CancellationToken token)
    {
        if(song.Source=="网易云")return await NetEaseLyrics(song.Id,token);
        if(song.Source=="QQ音乐")return await QqLyrics(song.Id,token);
        if(song.Source=="酷狗"){var p=song.Id.Split('|',2);if(p.Length==2){var bytes=await Get($"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(p[0])}&accesskey={Uri.EscapeDataString(p[1])}&fmt=lrc&charset=utf8",2*1024*1024,token);if(bytes!=null){using var doc=JsonDocument.Parse(bytes);var content=doc.RootElement.TryGetProperty("content",out var c)?c.GetString():null;if(!string.IsNullOrWhiteSpace(content))return Encoding.UTF8.GetString(Convert.FromBase64String(content));}}}
        return null;
    }
    async Task<string?> QqLyrics(string mid,CancellationToken token)
    {
        var bytes=await Get($"https://i.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(mid)}&format=json&nobase64=1",2*1024*1024,token);if(bytes==null)return null;using var doc=JsonDocument.Parse(bytes);var root=doc.RootElement;return root.TryGetProperty("lyric",out var l)?l.GetString():null;
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
        var all=new List<CoverCandidate>();all.AddRange(await SafeCovers(() => NetEaseCovers(album,artist,title,token)));all.AddRange(await SafeCovers(() => QqCovers(album,artist,title,token)));all.AddRange(await SafeCovers(() => ItunesCovers(album,artist,title,token)));
        if(all.Count>0)return all.GroupBy(c=>$"{c.Source}:{c.ReleaseId}").Select(g=>g.First()).ToList();
        static string Clean(string s)=>s.Replace("\\","").Replace("\"","");
        var query=$"release:\"{Clean(album)}\" AND artist:\"{Clean(artist)}\"";
        var bytes=await Get("https://musicbrainz.org/ws/2/release/?fmt=json&limit=8&query="+Uri.EscapeDataString(query),2*1024*1024,token);
        if(bytes==null)return [];
        using var doc=JsonDocument.Parse(bytes);var list=new List<CoverCandidate>();
        foreach(var r in doc.RootElement.GetProperty("releases").EnumerateArray()){
            var id=r.GetProperty("id").GetString()!;if(!Guid.TryParse(id,out _))continue;
            string artistCredit=r.TryGetProperty("artist-credit",out var credits)?string.Join(" / ",credits.EnumerateArray().Where(a=>a.TryGetProperty("name",out _)).Select(a=>a.GetProperty("name").GetString())):"";
            list.Add(new(r.GetProperty("title").GetString()??"",artistCredit,id,$"https://coverartarchive.org/release/{id}/front-500","MusicBrainz"));
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
                        var candidate=new CoverCandidate(a.GetProperty("name").GetString()??"",artistName,$"ne{id}",pic,"网易云");
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
    async Task<List<CoverCandidate>> QqCovers(string album,string artist,string title,CancellationToken token)
    {
        var query=string.Join(" ",new[]{album,artist,title}.Where(x=>!string.IsNullOrWhiteSpace(x)));if(query.Length==0)return [];
        var payload=new Dictionary<string,object>{{"music.search.SearchCgiService",new Dictionary<string,object>{{"method","DoSearchForQQMusicDesktop"},{"module","music.search.SearchCgiService"},{"param",new Dictionary<string,object>{{"num_per_page",12},{"page_num",1},{"query",query},{"search_type",2}}}}}};
        using var response=await client.PostAsync("https://u.y.qq.com/cgi-bin/musicu.fcg",JsonContent.Create(payload),token);if(!response.IsSuccessStatusCode)return [];using var doc=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));var root=doc.RootElement.GetProperty("music.search.SearchCgiService").GetProperty("data").GetProperty("body");if(!root.TryGetProperty("album",out var albums))return [];var list=new List<CoverCandidate>();foreach(var a in albums.GetProperty("list").EnumerateArray()){var mid=a.GetProperty("mid").GetString()??"";var name=a.GetProperty("name").GetString()??"";var singer=a.TryGetProperty("singer",out var ss)?string.Join(" / ",ss.EnumerateArray().Select(x=>x.GetProperty("name").GetString())):"";if(mid.Length>0)list.Add(new(name,singer,$"qq{mid}",$"https://y.gtimg.cn/music/photo_new/T002R500x500M000{mid}.jpg","QQ音乐"));}return list;
    }
    async Task<List<CoverCandidate>> ItunesCovers(string album,string artist,string title,CancellationToken token)
    {
        var term=Uri.EscapeDataString(string.Join(" ",new[]{album,artist,title}.Where(x=>!string.IsNullOrWhiteSpace(x))));var bytes=await Get($"https://itunes.apple.com/search?term={term}&media=music&entity=album&limit=8",2*1024*1024,token);if(bytes==null)return [];using var doc=JsonDocument.Parse(bytes);if(!doc.RootElement.TryGetProperty("results",out var results))return [];var list=new List<CoverCandidate>();foreach(var r in results.EnumerateArray()){var url=r.TryGetProperty("artworkUrl100",out var u)?u.GetString()??"":"";if(url.Length==0)continue;url=url.Replace("100x100bb", "1000x100bb");list.Add(new(r.GetProperty("collectionName").GetString()??"",r.GetProperty("artistName").GetString()??"",$"itunes{r.GetProperty("collectionId").GetInt64()}",url,"iTunes"));}return list;
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
