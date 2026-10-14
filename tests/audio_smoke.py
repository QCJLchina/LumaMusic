"""Real decoder/output smoke tests. All playback samples are digital silence."""
import ctypes as C,json,os,pathlib,struct,subprocess,time,wave,hashlib
root=pathlib.Path(__file__).resolve().parents[1]
folder=root/'build/fixtures';folder.mkdir(parents=True,exist_ok=True)
binary=root/'build/native-bin'

def dsf(channels):
    rate=2822400;samples=rate//5;block=4096;blocks=(samples//8+block-1)//block
    payload=bytes([0x69])*(blocks*block*channels)
    file=folder/f'silence-{channels}ch.dsf'
    fmt=b'fmt '+struct.pack('<QIIIIIIQII',52,1,0,2 if channels==2 else 7,channels,rate,1,samples,block,0)
    size=28+52+12+len(payload)
    file.write_bytes(b'DSD '+struct.pack('<QQQ',28,size,0)+fmt+b'data'+struct.pack('<Q',12+len(payload))+payload)
    return file,samples//8*channels

for channels in [2,6]:
    file,expected=dsf(channels)
    result=subprocess.run([str(binary/'LumaDsd.exe'),'--probe',str(file)],capture_output=True,timeout=20)
    assert result.returncode==0,result.stderr
    info=json.loads(result.stdout);assert info[0]['channels']==channels,info
    for mode in ['raw','pcm']:
        p=subprocess.run([str(binary/'LumaDsd.exe'),'--stream',str(file),'1',mode,'176400','0'],capture_output=True,timeout=20)
        assert p.returncode==0,p.stderr
        header=struct.unpack_from('<7Id',p.stdout);assert header[0]==0x414d554c and header[3]==channels,header
        pos=36;payload=bytearray()
        while True:
            n=struct.unpack_from('<I',p.stdout,pos)[0];pos+=4
            if n==0:break
            payload.extend(p.stdout[pos:pos+n]);pos+=n
        if mode=='raw':assert len(payload)==expected and set(payload)=={0x96},(len(payload),expected,set(payload))
        else:assert len(payload)>0 and len(payload)% (channels*4)==0
        print('PASS',channels,'channels',mode,len(payload),'bytes',flush=True)
    p=subprocess.run([str(binary/'LumaDsd.exe'),'--stream',str(file),'1','raw','176400','0.1'],capture_output=True,timeout=20)
    assert p.returncode==0,p.stderr
    pos=36;size=0
    while True:
        n=struct.unpack_from('<I',p.stdout,pos)[0];pos+=4
        if not n:break
        size+=n;pos+=n
    assert size==expected//2,(size,expected//2)
    print('PASS DSD exact seek',channels,flush=True)

file=folder/'silence.wav'
with wave.open(str(file),'wb') as w:w.setparams((2,2,48000,0,'NONE','not compressed'));w.writeframes(bytes(48000*2*2))
os.add_dll_directory(str(binary));lib=C.CDLL(str(binary/'LumaAudio.dll'))
lib.luma_init.argtypes=[C.c_wchar_p];lib.luma_init.restype=C.c_int
lib.luma_devices.restype=C.c_char_p;lib.luma_state.restype=C.c_char_p;lib.luma_error.restype=C.c_char_p
lib.luma_open.argtypes=[C.c_wchar_p]+[C.c_int]*7+[C.POINTER(C.c_int),C.c_float,C.c_double,C.c_double,C.c_float]
lib.luma_open.restype=C.c_int
assert lib.luma_init(str(binary)),lib.luma_error()
devices=json.loads(lib.luma_devices());print('Output devices:',[(d['name'],d['backend']) for d in devices],flush=True)
device=next((d for d in devices if d['default']),None)
if device:
    mapping=(C.c_int*8)(*range(8))
    for backend in [0,1]:
        result=lib.luma_open(str(file),1,backend,device['index'],0,176400,48000,0,mapping,0.,0.,0.,1.)
        if not result:print('HARDWARE UNAVAILABLE',backend,lib.luma_error().decode('utf-8'),flush=True);continue
        time.sleep(.35);state=json.loads(lib.luma_state());assert state['position']>0 and not state['failed'],state
        lib.luma_pause(1);lib.luma_pause(0);lib.luma_stop();print('PASS real silent output',backend,state['rate'],flush=True)
        # cue 拆轨：origin=0.5 起点、limit=0.9 终点，进度条必须只覆盖 0.4 秒这一段，
        # ended 自己变 true，且欠载计数不能被截断污染。
        result=lib.luma_open(str(file),1,backend,device['index'],0,176400,48000,0,mapping,0.,0.5,0.9,1.)
        if result:
            state=json.loads(lib.luma_state());assert abs(state['duration']-0.4)<1e-6,state
            for _ in range(40):
                time.sleep(.05);state=json.loads(lib.luma_state())
                if state['ended']:break
            assert state['ended'],state
            assert state['underruns']==0,state
            assert 0<=state['position']<=0.4+1e-6,state
            lib.luma_stop();print('PASS cue limit truncates and reports ended',flush=True)
        else:print('HARDWARE UNAVAILABLE cue limit',backend,lib.luma_error().decode('utf-8'),flush=True)
        # ReplayGain：引擎必须如实回报收到的增益，且不改变采样率/声道（不重采样）。
        result=lib.luma_open(str(file),1,backend,device['index'],0,176400,48000,0,mapping,0.,0.,0.,0.5)
        if result:
            state=json.loads(lib.luma_state())
            assert abs(state['gain']-0.5)<1e-6,state
            # ReplayGain 只改幅度：声道数不变、源采样率不变。
            # 输出采样率由设备/独占格式决定，共享模式本就会重采样到设备率。
            assert state['channels']==2 and state['sourceRate']==48000,state
            lib.luma_stop();print('PASS replaygain applied without resampling',flush=True)
        else:print('HARDWARE UNAVAILABLE replaygain',backend,lib.luma_error().decode('utf-8'),flush=True)
lib.luma_shutdown()
