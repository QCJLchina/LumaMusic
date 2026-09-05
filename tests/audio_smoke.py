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
lib.luma_open.argtypes=[C.c_wchar_p]+[C.c_int]*7+[C.POINTER(C.c_int),C.c_float,C.c_double]
lib.luma_open.restype=C.c_int
assert lib.luma_init(str(binary)),lib.luma_error()
devices=json.loads(lib.luma_devices());print('Output devices:',[(d['name'],d['backend']) for d in devices],flush=True)
device=next((d for d in devices if d['default']),None)
if device:
    mapping=(C.c_int*8)(*range(8))
    for backend in [0,1]:
        result=lib.luma_open(str(file),1,backend,device['index'],0,176400,48000,0,mapping,0.,0.)
        if not result:print('HARDWARE UNAVAILABLE',backend,lib.luma_error().decode('utf-8'),flush=True);continue
        time.sleep(.35);state=json.loads(lib.luma_state());assert state['position']>0 and not state['failed'],state
        lib.luma_pause(1);lib.luma_pause(0);lib.luma_stop();print('PASS real silent output',backend,state['rate'],flush=True)
lib.luma_shutdown()
