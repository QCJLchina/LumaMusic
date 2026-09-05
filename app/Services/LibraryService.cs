using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace LumaMusic.Services;
public class LibraryService
{
    static readonly HashSet<string> Extensions=new(StringComparer.OrdinalIgnoreCase){".mp3",".flac",".wav",".aiff",".aif",".aac",".m4a",".ogg",".opus",".ape",".wv",".dsf",".dff",".iso"};
    readonly string connectionString=$"Data Source={System.IO.Path.Combine(AppPaths.Root,"library.db")}";
    readonly SemaphoreSlim scanLock=new(1,1);
    SqliteConnection Connect(){var c=new SqliteConnection(connectionString);c.Open();return c;}
    public LibraryService(){SQLitePCL.Batteries_V2.Init();using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS tracks(id TEXT PRIMARY KEY,path TEXT NOT NULL,modified INTEGER NOT NULL,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS playlists(id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT NOT NULL); CREATE TABLE IF NOT EXISTS playlist_tracks(playlist INTEGER,track TEXT,position INTEGER,PRIMARY KEY(playlist,track)); CREATE TABLE IF NOT EXISTS lyrics(track TEXT PRIMARY KEY,text TEXT NOT NULL,offset REAL NOT NULL DEFAULT 0);";cmd.ExecuteNonQuery();}
    public List<Track> Load(){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT payload FROM tracks";using var r=cmd.ExecuteReader();var tracks=new List<Track>();while(r.Read()){try{var t=JsonSerializer.Deserialize<Track>(r.GetString(0),AppPaths.Json);if(t!=null)tracks.Add(t);}catch{}}return tracks.OrderBy(t=>t.Album,StringComparer.CurrentCultureIgnoreCase).ThenBy(t=>t.Path).ThenBy(t=>t.Subsong).ToList();}
    public void Save(Track track){using var c=Connect();Save(c,track);}
    static void Save(SqliteConnection c,Track t){using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO tracks VALUES($id,$path,$modified,$payload) ON CONFLICT(id) DO UPDATE SET path=excluded.path,modified=excluded.modified,payload=excluded.payload";cmd.Parameters.AddWithValue("$id",t.Id);cmd.Parameters.AddWithValue("$path",t.Path);cmd.Parameters.AddWithValue("$modified",t.Modified);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(t,AppPaths.Json));cmd.ExecuteNonQuery();}
    public static string Key(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public async Task<int> Import(IEnumerable<string> paths,IProgress<string> progress,CancellationToken token)
    {
        await scanLock.WaitAsync(token);
        try{return await Task.Run(async()=>{
            using var c=Connect();var existing=Load().GroupBy(t=>t.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
            int count=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var source in paths){
                IEnumerable<string> files=Directory.Exists(source)?Directory.EnumerateFiles(source,"*",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}):[source];
                foreach(var file in files){token.ThrowIfCancellationRequested();if(!Extensions.Contains(System.IO.Path.GetExtension(file))||!File.Exists(file)||!seen.Add(file))continue;
                    long modified=File.GetLastWriteTimeUtc(file).Ticks;
                    if(existing.TryGetValue(file,out var old)&&old.All(t=>t.Modified==modified))continue;
                    progress.Report($"正在读取 · {System.IO.Path.GetFileName(file)}");
                    try{var tracks=await ReadFile(file,token);foreach(var t in tracks){var previous=old?.FirstOrDefault(x=>x.Id==t.Id);t.Favorite=previous?.Favorite??false;if(!string.IsNullOrEmpty(previous?.Cover))t.Cover=previous.Cover;Save(c,t);count++;}}
                    catch(Exception e){AppPaths.Log($"Import {file}: {e.Message}");}
                }
            }
            return count;
        },token);}finally{scanLock.Release();}
    }
    async Task<List<Track>> ReadFile(string file,CancellationToken token)
    {
        string format=System.IO.Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
        var first=new Track{Id=Key(file.ToUpperInvariant()+"#1"),Path=file,Title=System.IO.Path.GetFileNameWithoutExtension(file),Format=format,Modified=File.GetLastWriteTimeUtc(file).Ticks};
        var tracks=new List<Track>();
        if(format is "DSF" or "DFF" or "ISO"){
            var start=new ProcessStartInfo(System.IO.Path.Combine(AppContext.BaseDirectory,"LumaDsd.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};start.ArgumentList.Add("--probe");start.ArgumentList.Add(file);
            using var p=Process.Start(start)??throw new IOException("DSD 读取器无法启动");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try{var read=p.StandardOutput.ReadToEndAsync(deadline.Token);var errors=p.StandardError.ReadToEndAsync(deadline.Token);await p.WaitForExitAsync(deadline.Token);if(p.ExitCode!=0)throw new IOException(await errors);using var doc=JsonDocument.Parse(await read);foreach(var t in doc.RootElement.EnumerateArray()){
                int sub=t.GetProperty("subsong").GetInt32();tracks.Add(new(){Id=Key(file.ToUpperInvariant()+"#"+sub),Path=file,Subsong=sub,Title=t.GetProperty("title").GetString() is {Length:>0} name?name:first.Title+(format=="ISO"?$" · {sub:00}":""),Artist=t.GetProperty("artist").GetString() is {Length:>0} artist?artist:first.Artist,Album=t.GetProperty("album").GetString() is {Length:>0} album?album:System.IO.Path.GetFileNameWithoutExtension(file),Duration=t.GetProperty("duration").GetDouble(),SampleRate=t.GetProperty("sampleRate").GetInt32(),Channels=t.GetProperty("channels").GetInt32(),Bits=1,Format=format,Modified=first.Modified});
            }}finally{if(!p.HasExited)p.Kill(true);}
        }else tracks.Add(first);
        try{
            using var tag=TagLib.File.Create(file);var picture=tag.Tag.Pictures.FirstOrDefault();string cover="";
            if(picture!=null&&picture.Data.Count<=12*1024*1024){cover=System.IO.Path.Combine(AppPaths.Cache,Key(file)+"-embedded.img");await File.WriteAllBytesAsync(cover,picture.Data.Data,token);}
            foreach(var t in tracks){if(format!="ISO"){
                if(!string.IsNullOrWhiteSpace(tag.Tag.Title))t.Title=tag.Tag.Title;
                if(tag.Tag.Performers.Length>0)t.Artist=string.Join(" / ",tag.Tag.Performers);
                if(!string.IsNullOrWhiteSpace(tag.Tag.Album))t.Album=tag.Tag.Album;
                if(t.Duration==0)t.Duration=tag.Properties.Duration.TotalSeconds;
                if(t.SampleRate==0)t.SampleRate=tag.Properties.AudioSampleRate;
                if(t.Bits==0)t.Bits=tag.Properties.BitsPerSample;
                if(tag.Properties.AudioChannels>0)t.Channels=tag.Properties.AudioChannels;
            }t.Cover=cover;}
        }catch(Exception e){if(format is not ("DSF" or "DFF" or "ISO"))AppPaths.Log($"Tags: {e.Message}");}
        foreach(var t in tracks){if(string.IsNullOrEmpty(t.Cover)){var dir=System.IO.Path.GetDirectoryName(file)!;t.Cover=new[]{"cover.jpg","folder.jpg","cover.png","folder.png"}.Select(x=>System.IO.Path.Combine(dir,x)).FirstOrDefault(File.Exists)??"";}}
        return tracks;
    }
    public List<Playlist> Playlists(){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT id,name FROM playlists ORDER BY id";using var r=cmd.ExecuteReader();var list=new List<Playlist>();while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1)));return list;}
    public void CreatePlaylist(string name){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO playlists(name) VALUES($name)";cmd.Parameters.AddWithValue("$name",name);cmd.ExecuteNonQuery();}
    public void AddToPlaylist(long id,string track){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO playlist_tracks VALUES($p,$t,(SELECT COALESCE(MAX(position),0)+1 FROM playlist_tracks WHERE playlist=$p))";cmd.Parameters.AddWithValue("$p",id);cmd.Parameters.AddWithValue("$t",track);cmd.ExecuteNonQuery();}
    public HashSet<string> PlaylistTracks(long id){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT track FROM playlist_tracks WHERE playlist=$p ORDER BY position";cmd.Parameters.AddWithValue("$p",id);using var r=cmd.ExecuteReader();var list=new HashSet<string>();while(r.Read())list.Add(r.GetString(0));return list;}
    public (string Text,double Offset) Lyrics(string id){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="SELECT text,offset FROM lyrics WHERE track=$t";cmd.Parameters.AddWithValue("$t",id);using var r=cmd.ExecuteReader();return r.Read()?(r.GetString(0),r.GetDouble(1)):("",0);}
    public void SaveLyrics(string id,string text,double offset=0){using var c=Connect();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO lyrics VALUES($t,$text,$offset) ON CONFLICT(track) DO UPDATE SET text=excluded.text,offset=excluded.offset";cmd.Parameters.AddWithValue("$t",id);cmd.Parameters.AddWithValue("$text",text);cmd.Parameters.AddWithValue("$offset",offset);cmd.ExecuteNonQuery();}
}
