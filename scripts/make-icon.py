"""Create our original geometric record icon, without external artwork."""
import struct,math,zlib
from pathlib import Path
root=Path(__file__).resolve().parents[1]
out=root/'app/Assets';out.mkdir(parents=True,exist_ok=True)
w=128;data=bytearray()
for y in range(w):
 data.append(0)
 for x in range(w):
  r=math.hypot(x-63.5,y-63.5)
  if r>60: color=(0,0,0,0)
  elif r<8:color=(20,35,29,255)
  elif r<20:color=(205,235,179,255)
  elif any(abs(r-v)<.8 for v in [31,40,49,58]):color=(153,181,139,255)
  else:color=(40+int(x/8),66+int(x/9),51+int(y/10),255)
  data.extend(color)
def chunk(name,content):return struct.pack('>I',len(content))+name+content+struct.pack('>I',zlib.crc32(name+content)&0xffffffff)
png=b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,w,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(data))+chunk(b'IEND',b'')
(out/'Luma.ico').write_bytes(struct.pack('<HHH',0,1,1)+struct.pack('<BBBBHHII',128,128,0,0,1,32,len(png),22)+png)
print('Luma icon ready')
