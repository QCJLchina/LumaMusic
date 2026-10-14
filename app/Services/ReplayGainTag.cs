namespace LumaMusic.Services;

/// ReplayGain 标签读取。取值全部来自文件里已有的标签，不做任何音频分析，
/// 所以结果与写入标签的工具（foobar2000 / MP3Gain 等）完全一致。
/// TagLib 的 Tag 基类已经映射好了 REPLAYGAIN_* 四个标准字段，各格式通用，
/// 所以这里不需要按 ID3v2/FLAC 分别处理。
public static class ReplayGainTag
{
    public record Values(double TrackGainDb,double AlbumGainDb,double TrackPeak,double AlbumPeak);

    /// <summary>标签没有 ReplayGain 时四项都是 0。</summary>
    public static Values Read(TagLib.Tag tag)
    {
        // 某些工具用 ±999 dB 表示"没算"，按缺失处理，避免给音量乘一个荒谬的倍率。
        static double Clean(double v)=>double.IsFinite(v)&&Math.Abs(v)<=100?v:0;
        return new(Clean(tag.ReplayGainTrackGain),Clean(tag.ReplayGainAlbumGain),
                   Clean(tag.ReplayGainTrackPeak),Clean(tag.ReplayGainAlbumPeak));
    }

    // 按设置里的模式挑一个增益值：0 关闭 / 1 专辑 / 2 歌曲。
    // 歌曲增益缺失时退回专辑增益——只打过一种标签的文件也能吃到修正。
    public static double Select(Values values,int mode)
    {
        if(mode==1)return values.AlbumGainDb;
        return mode==2?values.TrackGainDb:(values.TrackGainDb!=0?values.TrackGainDb:values.AlbumGainDb);
    }

    /// <summary>选中增益对应的真峰值（dBFS，≤0），用于提示增益后可能削顶。</summary>
    public static double PeakFor(Values values,int mode)
        =>mode switch{1=>values.AlbumPeak,2=>values.TrackPeak,_=>values.TrackPeak!=0?values.TrackPeak:values.AlbumPeak};
}