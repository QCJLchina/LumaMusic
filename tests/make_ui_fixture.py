"""Create an isolated, offline UI library with generated art and digital silence."""
import argparse, json, pathlib, sqlite3, struct, wave
from PIL import Image, ImageDraw, ImageFilter

ROOT = pathlib.Path(__file__).resolve().parents[1]

def create(profile: pathlib.Path):
    profile = profile.resolve()
    if not profile.is_relative_to((ROOT / 'build').resolve()):
        raise ValueError('UI fixtures must live under build/')
    profile.mkdir(parents=True, exist_ok=True)
    assets = ROOT / 'build/ui-fixtures'
    assets.mkdir(parents=True, exist_ok=True)
    silent = assets / 'silence.wav'
    with wave.open(str(silent), 'wb') as stream:
        stream.setparams((2, 2, 48000, 0, 'NONE', 'not compressed'))
        stream.writeframes(bytes(48000 * 4 * 60))
    albums = [('月光海岸', '林间回声', (40, 77, 125), (216, 176, 155)),
              ('橘色日落', '缓慢行星', (150, 68, 54), (255, 211, 152)),
              ('静谧森林', '林间回声', (36, 80, 75), (168, 194, 146)),
              ('午夜电台', '城市漫游', (32, 34, 85), (157, 134, 200)),
              ('白日梦', '晴空', (146, 178, 196), (251, 237, 215)),
              ('山间来信', '远山', (98, 115, 91), (204, 190, 155)),
              ('潮汐', '缓慢行星', (34, 112, 151), (187, 228, 223)),
              ('漫长的告别 · 一张用于验证长标题截断与布局的专辑', '城市漫游', (124, 69, 91), (242, 194, 178)),
              ('纯黑封面', '边界样本', (0, 0, 0), (0, 0, 0)),
              ('纯白封面', '边界样本', (255, 255, 255), (255, 255, 255)),
              ('鲜艳封面', '边界样本', (250, 0, 90), (0, 240, 255)),
              ('无封面', '边界样本', (70, 70, 70), (90, 90, 90))]
    db = sqlite3.connect(profile / 'library.db')
    db.executescript('CREATE TABLE IF NOT EXISTS tracks(id TEXT PRIMARY KEY,path TEXT NOT NULL,modified INTEGER NOT NULL,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS playlists(id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT NOT NULL); CREATE TABLE IF NOT EXISTS playlist_tracks(playlist INTEGER,track TEXT,position INTEGER,PRIMARY KEY(playlist,track)); CREATE TABLE IF NOT EXISTS lyrics(track TEXT PRIMARY KEY,text TEXT NOT NULL,offset REAL NOT NULL DEFAULT 0);')
    for i, (album, artist, top, bottom) in enumerate(albums):
        cover = assets / f'cover-{i}.png'
        image = Image.new('RGB', (512, 512))
        draw = ImageDraw.Draw(image)
        for y in range(512):
            t = y / 511
            draw.line((0, y, 512, y), fill=tuple(round(a*(1-t)+b*t) for a,b in zip(top,bottom)))
        if i < 8:
            draw.ellipse((160, 85, 330, 255), fill=bottom)
            draw.polygon([(0, 420), (160, 250), (330, 385), (512, 240), (512,512), (0,512)], fill=top)
            image = image.filter(ImageFilter.GaussianBlur(2))
        image.save(cover)
        for j in range(3):
            key = f'ui-{i}-{j}'
            track = dict(Id=key, Path=str(silent), Subsong=1, Title=['风经过的时候', '慢慢靠近', '留在这一刻'][j],
                         Artist=artist, Album=album, Duration=60, SampleRate=48000, Channels=2, Bits=16,
                         Format='WAV', Modified=0, Cover=str(cover) if i != 11 else '', Favorite=j==0)
            db.execute('INSERT OR REPLACE INTO tracks VALUES(?,?,?,?)', (key,str(silent),0,json.dumps(track,ensure_ascii=False)))
            db.execute('INSERT OR REPLACE INTO lyrics VALUES(?,?,?)', (key,'\n'.join(f'[{k//60:02}:{k%60:02}.00]{line}' for k,line in enumerate(['窗外的风轻轻经过','让时间慢一点流动','听见远方的回声','把心事交给夜空','每一刻都值得停留','在音乐里自由漫游','UI 验证用歌词 · 数字静音'],0)),0))
    db.execute('INSERT OR IGNORE INTO playlists VALUES(1,?)', ('夜晚的散步',))
    db.execute('INSERT OR IGNORE INTO playlist_tracks VALUES(1,?,0)', ('ui-0-0',))
    db.commit(); db.close()
    create_cue_album(assets)
    (profile/'settings.json').write_text(json.dumps(dict(Online=False,Theme=1,Volume=0,LastTrack='ui-0-0',Queue=['ui-0-0','ui-1-0','ui-2-0'],CloseToTray=False,LastUpdateCheck='2026-09-07T00:00:00Z')),encoding='utf-8')
    print(profile)

def create_cue_album(assets: pathlib.Path):
    """一张整轨 WAV + 同名 .cue：走真实导入路径验证 cue 拆轨，而不是直接塞数据库行。"""
    album = assets / 'cue-album'
    album.mkdir(parents=True, exist_ok=True)
    whole = album / '整轨.wav'
    # 30 秒立体声静音，cue 在 5/15/25 秒切三首
    with wave.open(str(whole), 'wb') as stream:
        stream.setparams((2, 2, 48000, 0, 'NONE', 'not compressed'))
        stream.writeframes(bytes(48000 * 4 * 30))
    # GBK 编码：中文 cue 在国内极常见，必须能解码
    cue = album / '整轨.cue'
    text = '\r\n'.join([
        'REM GENRE 测试流派',
        'REM DATE 1997',
        'REM ALBUM 整轨专辑',
        'REM ALBUMARTIST 整轨乐队',
        'FILE "整轨.wav" WAVE',
        '  TRACK 01 AUDIO',
        '    TITLE 第一首',
        '    PERFORMER 整轨乐队',
        '    INDEX 01 00:00:00',
        '  TRACK 02 AUDIO',
        '    TITLE 第二首',
        '    PERFORMER 整轨乐队',
        '    INDEX 00 00:04:74',
        '    INDEX 01 00:05:00',
        '  TRACK 03 AUDIO',
        '    TITLE 第三首',
        '    PERFORMER 整轨乐队',
        '    INDEX 01 00:15:00',
        ''])
    cue.write_bytes(text.encode('gbk'))
    create_replaygain_sample(assets)

def riff_chunk(fourcc: bytes, body: bytes) -> bytes:
    """RIFF 子块：4 字节 ID + 4 字节长度（不含补位）+ 数据 + 奇数字节补 0。"""
    return fourcc + struct.pack('<I', len(body)) + body + (b'\x00' if len(body) % 2 else b'')

def id3v2_frame(frame_id: bytes, payload: bytes) -> bytes:
    """单个 ID3v2.3 帧：帧 ID + syncsafe 长度 + 标志 + 载荷。"""
    size = len(payload)
    sync = bytes([(size >> 21) & 0x7f, (size >> 14) & 0x7f, (size >> 7) & 0x7f, size & 0x7f])
    return frame_id + sync + b'\x00\x00' + payload

def id3v2_txxx(description: str, value: str) -> bytes:
    """TXXX 私有帧：UTF-8 编码标记 + 描述符 + \\x00 + 值。TagLib 的 ReplayGain 读的就是它。"""
    return id3v2_frame(b'TXXX', b'\x03' + description.encode('utf-8') + b'\x00' + value.encode('utf-8'))

def id3v2_text(frame_id: bytes, text: str) -> bytes:
    return id3v2_frame(frame_id, b'\x03' + text.encode('utf-8'))

def id3v2_tag(frames: bytes) -> bytes:
    size = len(frames)
    sync = bytes([(size >> 21) & 0x7f, (size >> 14) & 0x7f, (size >> 7) & 0x7f, size & 0x7f])
    return b'ID3' + b'\x03\x00' + b'\x00' + sync + frames

def create_replaygain_sample(assets: pathlib.Path):
    """一张带 ReplayGain 标签的 WAV（内嵌 ID3v2）：标签读取走真实解析，不硬编码数值。"""
    path = assets / 'replaygain-sample.wav'
    fmt = struct.pack('<HHIIHH', 1, 2, 48000, 48000 * 4, 4, 16)
    gain_db = -6.5
    tag = id3v2_tag(
        id3v2_text(b'TIT2', 'ReplayGain 样本') +
        id3v2_txxx('REPLAYGAIN_TRACK_GAIN', f'{gain_db:.2f} dB'))
    chunks = riff_chunk(b'fmt ', fmt) + riff_chunk(b'data', bytes(48000 * 4 * 2)) + riff_chunk(b'id3 ', tag)
    data = b'WAVE' + chunks
    path.write_bytes(b'RIFF' + struct.pack('<I', len(data)) + data)
    (assets / 'replaygain-expected.json').write_text(json.dumps({'trackGain': gain_db}), encoding='utf-8')

if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--profile',default='build/ui-test-profile')
    create(ROOT / parser.parse_args().profile)
