using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
namespace LumaMusic.Services;
public class LibraryService
{
    static readonly HashSet<string> Extensions=new(StringComparer.OrdinalIgnoreCase){".mp3",".flac",".wav",".aiff",".aif",".aac",".m4a",".ogg",".opus",".ape",".wv",".dsf",".dff",".iso"};
    readonly string connectionString=$"Data Source={System.IO.Path.Combine(AppPaths.Root,"library.db")}";
    readonly SemaphoreSlim scanLock=new(1,1);
    readonly ConcurrentDictionary<string,byte> removed=new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string,byte> removedPaths=new(StringComparer.OrdinalIgnoreCase);
    SqliteConnection Connect(){var c=new SqliteConnection(connectionString);c.Open();return c;}
    public LibraryService(){SQLitePCL.Batteries_V2.Init();using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS tracks(id TEXT PRIMARY KEY,path TEXT NOT NULL,modified INTEGER NOT NULL,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS playlists(id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT NOT NULL); CREATE TABLE IF NOT EXISTS playlist_tracks(playlist INTEGER,track TEXT,position INTEGER,PRIMARY KEY(playlist,track)); CREATE TABLE IF NOT EXISTS lyrics(track TEXT PRIMARY KEY,text TEXT NOT NULL,offset REAL NOT NULL DEFAULT 0);";cmd.ExecuteNonQuery();}
    // 专辑内按碟号→曲号排；两字段都缺（0）时靠路径兜底，保证同一张碟内顺序稳定不跳。
    public List<Track> Load(){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT payload FROM tracks";using var r=cmd.ExecuteReader();var tracks=new List<Track>();while(r.Read()){try{var t=JsonSerializer.Deserialize<Track>(r.GetString(0),AppPaths.Json);if(t!=null)tracks.Add(t);}catch{}}return tracks.OrderBy(t=>t.Album,StringComparer.CurrentCultureIgnoreCase).ThenBy(t=>t.AlbumArtist,StringComparer.CurrentCultureIgnoreCase).ThenBy(t=>t.DiscNumber).ThenBy(t=>t.TrackNumber).ThenBy(t=>t.Path,StringComparer.OrdinalIgnoreCase).ThenBy(t=>t.Subsong).ToList();}
    public void Save(Track track){if(removed.ContainsKey(track.Id))return;using var c=Connect();Save(c,track);}
    static void Save(SqliteConnection c,Track t){using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO tracks VALUES($id,$path,$modified,$payload) ON CONFLICT(id) DO UPDATE SET path=excluded.path,modified=excluded.modified,payload=excluded.payload";cmd.Parameters.AddWithValue("$id",t.Id);cmd.Parameters.AddWithValue("$path",t.Path);cmd.Parameters.AddWithValue("$modified",t.Modified);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(t,AppPaths.Json));cmd.ExecuteNonQuery();}
    public static string Key(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    // 曲目"版本戳"：音频文件 + 同名 cue 的 mtime。cue 改动会改变拆出的曲目，必须一起纳入。
    static long Stamp(string file)
    {
        long stamp=File.GetLastWriteTimeUtc(file).Ticks;
        var cue=System.IO.Path.ChangeExtension(file,".cue");
        if(File.Exists(cue))stamp^=File.GetLastWriteTimeUtc(cue).Ticks;
        return stamp;
    }
    public async Task<int> Import(IEnumerable<string> paths,IProgress<string> progress,CancellationToken token,bool force=false)
    {
        await scanLock.WaitAsync(token);
        try{return await Task.Run(async()=>{
            using var c=Connect();var existing=Load().GroupBy(t=>t.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
            int count=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var source in paths){
                IEnumerable<string> files=Directory.Exists(source)?Directory.EnumerateFiles(source,"*",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}):[source];
                foreach(var file in files){token.ThrowIfCancellationRequested();if(!Extensions.Contains(System.IO.Path.GetExtension(file))||!File.Exists(file)||!seen.Add(file))continue;
                    long modified=Stamp(file);
                    // force 跳过 mtime 短路：用于补齐历史库缺失的元数据（碟号曲号等），否则永远读不到。
                    existing.TryGetValue(file,out var old);
                    if(!force&&old!=null&&old.Count>0&&old.All(t=>t.Modified==modified)&&!removedPaths.ContainsKey(file))continue;
                    progress.Report($"正在读取 · {System.IO.Path.GetFileName(file)}");
                    try{var tracks=await ReadFile(file,token);foreach(var t in tracks){removed.TryRemove(t.Id,out _);var previous=old?.FirstOrDefault(x=>x.Id==t.Id);t.Favorite=previous?.Favorite??false;if(!string.IsNullOrEmpty(previous?.Cover))t.Cover=previous.Cover;Save(c,t);count++;}removedPaths.TryRemove(file,out _);}
                    catch(Exception e){AppPaths.Log($"Import {file}: {e.Message}");}
                }
            }
            return count;
        },token);}finally{scanLock.Release();}
    }
    async Task<List<Track>> ReadFile(string file,CancellationToken token)
    {
        string format=System.IO.Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
        var first=new Track{Id=Key(file.ToUpperInvariant()+"#1"),Path=file,Title=System.IO.Path.GetFileNameWithoutExtension(file),Format=format,Modified=Stamp(file)};
        var tracks=new List<Track>();
        if(format is "DSF" or "DFF" or "ISO"){
            var start=new ProcessStartInfo(System.IO.Path.Combine(AppContext.BaseDirectory,"LumaDsd.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};start.ArgumentList.Add("--probe");start.ArgumentList.Add(file);
            using var p=Process.Start(start)??throw new IOException("DSD 读取器无法启动");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try{var read=p.StandardOutput.ReadToEndAsync(deadline.Token);var errors=p.StandardError.ReadToEndAsync(deadline.Token);await p.WaitForExitAsync(deadline.Token);if(p.ExitCode!=0)throw new IOException(await errors);using var doc=JsonDocument.Parse(await read);foreach(var t in doc.RootElement.EnumerateArray()){
                int sub=t.GetProperty("subsong").GetInt32();tracks.Add(new(){Id=Key(file.ToUpperInvariant()+"#"+sub),Path=file,Subsong=sub,Title=t.GetProperty("title").GetString() is {Length:>0} name?name:first.Title+(format=="ISO"?$" · {sub:00}":""),Artist=t.GetProperty("artist").GetString() is {Length:>0} artist?artist:first.Artist,Album=t.GetProperty("album").GetString() is {Length:>0} album?album:System.IO.Path.GetFileNameWithoutExtension(file),Duration=t.GetProperty("duration").GetDouble(),SampleRate=t.GetProperty("sampleRate").GetInt32(),Channels=t.GetProperty("channels").GetInt32(),Bits=1,Format=format,Modified=first.Modified});
            }}finally{if(!p.HasExited)p.Kill(true);}
        }else{
            // 整轨 + 同名 .cue：按 INDEX 拆成多首虚拟曲目。解析失败静默回退成整轨单曲。
            var sheet=format is "DSF" or "DFF" or "ISO"?null:CueSheet.TryLoad(file);
            double total=0;
            try{total=TagLib.File.Create(file).Properties.Duration.TotalSeconds;}catch{}
            if(sheet!=null&&sheet.Entries.Count>0){
                for(int i=0;i<sheet.Entries.Count;i++){
                    var e=sheet.Entries[i];
                    // 末曲时长用文件总长兜底，避免 cue 收尾缺 INDEX 时算出 0 秒。
                    double end=i+1<sheet.Entries.Count?sheet.Entries[i+1].Start:Math.Max(sheet.Entries[i].Start,total);
                    tracks.Add(new Track{
                        Id=Key(file.ToUpperInvariant()+"#"+e.Number),Path=file,Subsong=e.Number,Title=e.Title.Length>0?e.Title:first.Title,
                        Artist=e.Artist.Length>0?e.Artist:first.Artist,Album=sheet.Album.Length>0?sheet.Album:first.Album,
                        AlbumArtist=sheet.AlbumArtist,Genre=sheet.Genre,Year=int.TryParse(sheet.Year,out var y)?y:0,
                        TrackNumber=e.Number,Duration=Math.Max(0,end-e.Start),CueIndex=e.Start,FromCue=true,Format=format,Modified=first.Modified});
                }
            }else tracks.Add(first);
        }
        try{
            using var tag=TagLib.File.Create(file);var picture=tag.Tag.Pictures.FirstOrDefault();string cover="";
            if(picture!=null&&picture.Data.Count<=12*1024*1024){cover=System.IO.Path.Combine(AppPaths.Cache,Key(file)+"-embedded.img");await File.WriteAllBytesAsync(cover,picture.Data.Data,token);}
            foreach(var t in tracks){if(format!="ISO"){
                // cue 拆出的曲目自带标题/艺术家/专辑，绝不能被整轨的标签覆盖成同一个值。
                bool fromCue=t.FromCue;
                if(!fromCue){
                    if(!string.IsNullOrWhiteSpace(tag.Tag.Title))t.Title=tag.Tag.Title;
                    if(tag.Tag.Performers.Length>0)t.Artist=string.Join(" / ",tag.Tag.Performers);
                    if(!string.IsNullOrWhiteSpace(tag.Tag.Album))t.Album=tag.Tag.Album;
                }
                if(!fromCue&&tag.Tag.AlbumArtists.Length>0)t.AlbumArtist=string.Join(" / ",tag.Tag.AlbumArtists);
                if(tag.Tag.Genres.Length>0)t.Genre=string.Join(" / ",tag.Tag.Genres);
                // TagLib 的 Disc/Track/Year 是 uint，溢出值（如 0xFFFFFFFF）一律当缺失。
                if(tag.Tag.Disc>0&&tag.Tag.Disc<=9999)t.DiscNumber=(int)tag.Tag.Disc;
                if(tag.Tag.Track>0&&tag.Tag.Track<=9999)t.TrackNumber=(int)tag.Tag.Track;
                if(tag.Tag.Year>0&&tag.Tag.Year<=9999)t.Year=(int)tag.Tag.Year;
                if(t.Duration==0)t.Duration=tag.Properties.Duration.TotalSeconds;
                if(t.SampleRate==0)t.SampleRate=tag.Properties.AudioSampleRate;
                if(t.Bits==0)t.Bits=tag.Properties.BitsPerSample;
                if(tag.Properties.AudioChannels>0)t.Channels=tag.Properties.AudioChannels;
            }
            t.Cover=cover;}
            // ReplayGain 只从标签读（不做音频分析）。曲目与专辑增益分开存，
            // 具体用哪个由播放时的设置决定；cue 拆出的曲目回退到专辑增益。
            var gains=ReplayGainTag.Read(tag.Tag);
            if(gains.TrackGainDb!=0||gains.AlbumGainDb!=0)
                foreach(var t in tracks){
                    var trackGain=t.FromCue?0:gains.TrackGainDb;
                    t.ReplayGainDb=trackGain!=0?trackGain:gains.AlbumGainDb;
                    t.ReplayGainAlbumDb=gains.AlbumGainDb;
                    t.GainSource=trackGain!=0?"track":gains.AlbumGainDb!=0?"album":"";
                    t.ReplayGainPeak=Math.Max(gains.TrackPeak,gains.AlbumPeak);
                }
        }catch(Exception e){if(format is not ("DSF" or "DFF" or "ISO"))AppPaths.Log($"Tags: {e.Message}");}
        foreach(var t in tracks){if(string.IsNullOrEmpty(t.Cover)){var dir=System.IO.Path.GetDirectoryName(file)!;t.Cover=new[]{"cover.jpg","folder.jpg","cover.png","folder.png"}.Select(x=>System.IO.Path.Combine(dir,x)).FirstOrDefault(File.Exists)??"";}}
        return tracks;
    }
    // 导出 m3u：CRLF 换行 + UTF-8 BOM，老播放器才不把中文歌名读成乱码。
    // 路径写成相对 m3u 所在目录的相对路径（老播放器从 m3u 所在处解析），
    // 跨盘符时无法相对化，退回绝对路径。
    public static string? ExportM3u(string path,IEnumerable<Track> tracks)
    {
        string target;
        try{target=System.IO.Path.GetFullPath(path);}catch{return null;}
        var root=System.IO.Path.GetDirectoryName(target);
        if(string.IsNullOrEmpty(root))root=System.IO.Path.GetPathRoot(target)!;
        var lines=new List<string>{"#EXTM3U"};
        foreach(var track in tracks)
        {
            try{
                var full=System.IO.Path.GetFullPath(track.Path);
                if(!File.Exists(full))continue;
                string entry=full;
                if(Path.GetPathRoot(full)?.Equals(Path.GetPathRoot(root)??root,StringComparison.OrdinalIgnoreCase)==true)
                    entry=Path.GetRelativePath(root,full);
                lines.Add($"#EXTINF:-1,{track.Title}");
                lines.Add(entry);
            }catch{AppPaths.Log("m3u export: "+track.Path);}
        }
        if(lines.Count==1)return null;
        File.WriteAllText(target,string.Join("\r\n",lines)+"\r\n",new UTF8Encoding(true));
        return target;
    }
    public List<Playlist> Playlists(){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT id,name FROM playlists ORDER BY id";using var r=cmd.ExecuteReader();var list=new List<Playlist>();while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1)));return list;}
    public void CreatePlaylist(string name){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO playlists(name) VALUES($name)";cmd.Parameters.AddWithValue("$name",name);cmd.ExecuteNonQuery();}
    public void DeletePlaylist(long id){using var c=Connect();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM playlist_tracks WHERE playlist=$p;DELETE FROM playlists WHERE id=$p";cmd.Parameters.AddWithValue("$p",id);cmd.ExecuteNonQuery();tx.Commit();}
    public void AddToPlaylist(long id,string track){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO playlist_tracks VALUES($p,$t,(SELECT COALESCE(MAX(position),0)+1 FROM playlist_tracks WHERE playlist=$p))";cmd.Parameters.AddWithValue("$p",id);cmd.Parameters.AddWithValue("$t",track);cmd.ExecuteNonQuery();}
    public void RemoveFromPlaylist(long id,string track){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM playlist_tracks WHERE playlist=$p AND track=$t";cmd.Parameters.AddWithValue("$p",id);cmd.Parameters.AddWithValue("$t",track);cmd.ExecuteNonQuery();}
    public void AddToPlaylist(long id,IEnumerable<string> tracks){var ids=tracks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(ids.Length==0)return;using var c=Connect();using var tx=c.BeginTransaction();foreach(var track in ids){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT OR IGNORE INTO playlist_tracks VALUES($p,$t,(SELECT COALESCE(MAX(position),0)+1 FROM playlist_tracks WHERE playlist=$p))";cmd.Parameters.AddWithValue("$p",id);cmd.Parameters.AddWithValue("$t",track);cmd.ExecuteNonQuery();}tx.Commit();}
    public void RemoveFromPlaylist(long id,IEnumerable<string> tracks){var ids=tracks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(ids.Length==0)return;using var c=Connect();using var tx=c.BeginTransaction();foreach(var track in ids){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM playlist_tracks WHERE playlist=$p AND track=$t";cmd.Parameters.AddWithValue("$p",id);cmd.Parameters.AddWithValue("$t",track);cmd.ExecuteNonQuery();}tx.Commit();}
    public void SetFavorite(IEnumerable<string> tracks,bool favorite){var ids=tracks.Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);if(ids.Count==0)return;using var c=Connect();using var tx=c.BeginTransaction();foreach(var t in Load().Where(t=>ids.Contains(t.Id))){t.Favorite=favorite;using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE tracks SET payload=$payload WHERE id=$id";cmd.Parameters.AddWithValue("$id",t.Id);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(t,AppPaths.Json));cmd.ExecuteNonQuery();}tx.Commit();}
    public void RemoveTracks(IEnumerable<string> tracks){var ids=tracks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(ids.Length==0)return;var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);scanLock.Wait();foreach(var id in ids)removed[id]=0;try{using var c=Connect();using var tx=c.BeginTransaction();foreach(var id in ids){using(var lookup=c.CreateCommand()){lookup.Transaction=tx;lookup.CommandText="SELECT path FROM tracks WHERE id=$t";lookup.Parameters.AddWithValue("$t",id);if(lookup.ExecuteScalar() is string path){paths.Add(path);removedPaths[path]=0;}}using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM playlist_tracks WHERE track=$t;DELETE FROM lyrics WHERE track=$t;DELETE FROM tracks WHERE id=$t";cmd.Parameters.AddWithValue("$t",id);cmd.ExecuteNonQuery();}tx.Commit();}catch{foreach(var id in ids)removed.TryRemove(id,out _);foreach(var path in paths)removedPaths.TryRemove(path,out _);throw;}finally{scanLock.Release();}}
    // 保序返回：拖拽重排依赖这个顺序，HashSet 会把它丢掉。
    public List<string> PlaylistTracks(long id){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT track FROM playlist_tracks WHERE playlist=$p ORDER BY position";cmd.Parameters.AddWithValue("$p",id);using var r=cmd.ExecuteReader();var list=new List<string>();while(r.Read())list.Add(r.GetString(0));return list;}
    public void RenamePlaylist(long id,string name){if(string.IsNullOrWhiteSpace(name))return;using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE playlists SET name=$n WHERE id=$p";cmd.Parameters.AddWithValue("$n",name.Trim());cmd.Parameters.AddWithValue("$p",id);cmd.ExecuteNonQuery();}
    public void ClearPlaylist(long id){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM playlist_tracks WHERE playlist=$p";cmd.Parameters.AddWithValue("$p",id);cmd.ExecuteNonQuery();}
    // 按传入顺序重写整张列表。搜索过滤后拖拽只会带上可见子集，没出现的曲目按原相对顺序接在后面，避免被丢掉。
    public void ReorderPlaylist(long id,IReadOnlyList<string> ordered)
    {
        if(ordered.Count==0)return;
        using var c=Connect();using var tx=c.BeginTransaction();
        var current=new List<string>();
        using(var read=c.CreateCommand()){
            read.Transaction=tx;read.CommandText="SELECT track FROM playlist_tracks WHERE playlist=$p ORDER BY position";read.Parameters.AddWithValue("$p",id);
            using var rows=read.ExecuteReader();while(rows.Read())current.Add(rows.GetString(0));
        }
        var member=current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next=new List<string>();
        foreach(var track in ordered)if(member.Contains(track)&&seen.Add(track))next.Add(track);
        foreach(var track in current)if(seen.Add(track))next.Add(track);
        using(var del=c.CreateCommand()){del.Transaction=tx;del.CommandText="DELETE FROM playlist_tracks WHERE playlist=$p";del.Parameters.AddWithValue("$p",id);del.ExecuteNonQuery();}
        for(int i=0;i<next.Count;i++){
            using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO playlist_tracks(playlist,track,position) VALUES($p,$t,$pos)";
            cmd.Parameters.AddWithValue("$p",id);cmd.Parameters.AddWithValue("$t",next[i]);cmd.Parameters.AddWithValue("$pos",i);cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public (string Text,double Offset) Lyrics(string id){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT text,offset FROM lyrics WHERE track=$t";cmd.Parameters.AddWithValue("$t",id);using var r=cmd.ExecuteReader();return r.Read()?(r.GetString(0),r.GetDouble(1)):("",0);}
    public void SaveLyrics(string id,string text,double offset=0){if(removed.ContainsKey(id))return;using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO lyrics VALUES($t,$text,$offset) ON CONFLICT(track) DO UPDATE SET text=excluded.text,offset=excluded.offset";cmd.Parameters.AddWithValue("$t",id);cmd.Parameters.AddWithValue("$text",text);cmd.Parameters.AddWithValue("$offset",offset);cmd.ExecuteNonQuery();}
}
