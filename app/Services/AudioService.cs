using System.Runtime.InteropServices;
using System.Text.Json;
namespace LumaMusic.Services;
public sealed class AudioService : IDisposable
{
    const string Dll="LumaAudio.dll";
    [DllImport(Dll,CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]static extern int luma_init(string directory);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr luma_devices();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr luma_error();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr luma_state();
    [DllImport(Dll,CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]static extern int luma_open(string path,int track,int backend,int device,int mode,int pcmRate,int forceRate,int downmix,int[] map,float volume,double position);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]static extern int luma_pause(int pause);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]static extern void luma_stop();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]static extern void luma_volume(float volume);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]static extern void luma_shutdown();
    readonly SemaphoreSlim serial=new(1,1);
    public bool Busy {get;private set;}
    public AudioService(){if(luma_init(AppContext.BaseDirectory)==0)throw new InvalidOperationException(Error());}
    static string Error()=>Marshal.PtrToStringUTF8(luma_error())??"音频错误";
    public List<AudioDevice> Devices()=>JsonSerializer.Deserialize<List<AudioDevice>>(Marshal.PtrToStringUTF8(luma_devices())??"[]",AppPaths.Json)??[];
    public PlaybackState State()=>Busy?new():JsonSerializer.Deserialize<PlaybackState>(Marshal.PtrToStringUTF8(luma_state())??"{}",AppPaths.Json)??new();
    public async Task Open(Track track,AudioDevice device,DeviceProfile profile,float volume,double position=0)
    {
        await serial.WaitAsync();Busy=true;
        try{
            if(profile.DsdMode==1&&!profile.DopConfirmed&&track.Format is "DSF" or "DFF" or "ISO")throw new InvalidOperationException("请先确认这台 DAC 支持 DoP。");
            await Task.Run(()=>{if(luma_open(track.Path,track.Subsong,profile.Backend,device.Index,profile.DsdMode,profile.PcmRate,profile.ForceRate,profile.Downmix?1:0,profile.Mapping,volume,position)==0)throw new InvalidOperationException(Error());});
        }finally{Busy=false;serial.Release();}
    }
    public void Pause(bool pause){if(!Busy&&luma_pause(pause?1:0)==0)throw new InvalidOperationException(Error());}
    public void Volume(float volume){if(!Busy)luma_volume(volume);}
    public async Task Stop(){await serial.WaitAsync();Busy=true;try{await Task.Run(luma_stop);}finally{Busy=false;serial.Release();}}
    public void Dispose(){luma_shutdown();serial.Dispose();}
}
