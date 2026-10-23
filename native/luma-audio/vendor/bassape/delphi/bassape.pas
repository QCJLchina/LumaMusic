{
  BASSAPE 2.4 Delphi/Pascal unit
  Copyright (c) 2022-2025 Un4seen Developments Ltd.

  See the BASSAPE.CHM file for more detailed documentation
}

unit BASSAPE;

interface

{$IFDEF MSWINDOWS}
uses BASS, Windows;
{$ELSE}
uses BASS;
{$ENDIF}

const
  // BASS_CHANNELINFO type
  BASS_CTYPE_STREAM_APE        = $10700;

const
{$IFDEF MSWINDOWS}
  bassapedll = 'bassape.dll';
{$ENDIF}
{$IFDEF LINUX}
  bassapedll = 'libbassape.so';
{$ENDIF}
{$IFDEF ANDROID}
  bassapedll = 'libbassape.so';
{$ENDIF}
{$IFDEF MACOS}
  {$IFDEF IOS}
    bassapedll = 'bassape.framework/bassape';
  {$ELSE}
    bassapedll = 'libbassape.dylib';
  {$ENDIF}
{$ENDIF}

function BASS_APE_StreamCreateFile(filetype:DWORD; f:Pointer; offset,length:QWORD; flags:DWORD): HSTREAM; {$IFDEF MSWINDOWS}stdcall{$ELSE}cdecl{$ENDIF}; external bassapedll;
function BASS_APE_StreamCreateURL(url:PChar; offset:DWORD; flags:DWORD; proc:DOWNLOADPROC; user:Pointer): HSTREAM; {$IFDEF MSWINDOWS}stdcall{$ELSE}cdecl{$ENDIF}; external bassapedll;
function BASS_APE_StreamCreateFileUser(system,flags:DWORD; var procs:BASS_FILEPROCS; user:Pointer): HSTREAM; {$IFDEF MSWINDOWS}stdcall{$ELSE}cdecl{$ENDIF}; external bassapedll;

implementation

end.