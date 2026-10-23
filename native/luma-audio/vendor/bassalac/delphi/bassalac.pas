{
  BASSALAC 2.4 Delphi/Pascal unit
  Copyright (c) 2016-2025 Un4seen Developments Ltd.

  See the BASSALAC.CHM file for more detailed documentation
}

unit BASSALAC;

interface

{$IFDEF MSWINDOWS}
uses BASS, Windows;
{$ELSE}
uses BASS;
{$ENDIF}

const
  // BASS_CHANNELINFO type
  BASS_CTYPE_STREAM_ALAC        = $10e00;

const
{$IFDEF MSWINDOWS}
  bassalacdll = 'bassalac.dll';
{$ENDIF}
{$IFDEF LINUX}
  bassalacdll = 'libbassalac.so';
{$ENDIF}
{$IFDEF ANDROID}
  bassalacdll = 'libbassalac.so';
{$ENDIF}

function BASS_ALAC_StreamCreateFile(filetype: DWORD; f: Pointer; offset, length: QWORD; flags: DWORD): HSTREAM; {$IFDEF MSWINDOWS}stdcall{$ELSE}cdecl{$ENDIF}; external bassalacdll;
function BASS_ALAC_StreamCreateURL(url: PChar; offset, flags: DWORD; proc: DOWNLOADPROC; user: Pointer): HSTREAM; {$IFDEF MSWINDOWS}stdcall{$ELSE}cdecl{$ENDIF}; external bassalacdll;
function BASS_ALAC_StreamCreateFileUser(system, flags: DWORD; var procs: BASS_FILEPROCS; user: Pointer): HSTREAM; {$IFDEF MSWINDOWS}stdcall{$ELSE}cdecl{$ENDIF}; external bassalacdll;

implementation

end.