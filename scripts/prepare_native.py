"""Apply a small, reproducible host adapter to the pinned upstream SACD source."""
from pathlib import Path
import shutil
root=Path(__file__).resolve().parents[1]
original=root/'vendor/sacd/audiodecoder.sacd-Piers'
target=root/'build/sacd'
shutil.copytree(original,target,dirs_exist_ok=True)
# Reader uses only this setting. Replace the Kodi settings dependency, not the decoder.
(target/'src/Settings.h').write_text('''#pragma once
class CSACDSettings { public: static CSACDSettings& GetInstance(){static CSACDSettings s;return s;}
bool GetSeparateMultichannel() const {return false;} };
''',encoding='utf-8')
# Propagate seek failures rather than accepting a failed file seek.
p=target/'src/sacd/sacd_media.cpp'
s=p.read_text(encoding='utf-8').replace('media_file->Seek(position, mode);\n  return true;','return media_file->Seek(position, mode) >= 0;')
p.write_text(s,encoding='utf-8')
print('SACD host adapter ready')
p=target/'lib/libdsdpcm/DSDPCMConverterEngine.cpp'
s=p.read_text(encoding='utf-8').replace('conv_type = conv_type_e::UNKNOWN;','conv_type = conv_type_e::UNKNOWN;\n\tconv_fp64 = false;')
p.write_text(s,encoding='utf-8')
