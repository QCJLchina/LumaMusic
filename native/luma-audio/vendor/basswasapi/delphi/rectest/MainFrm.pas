unit MainFrm;

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Variants, System.Classes, Vcl.Graphics,
  Vcl.Controls, Vcl.Forms, Vcl.Dialogs, BASS, BASSmix, BASSWASAPI, Vcl.StdCtrls,
  Vcl.ComCtrls, Vcl.Samples.Gauges, Vcl.ExtCtrls;

type
  TfrmWASAPImicRec = class(TForm)
    cbDevices: TComboBox;
    gaMicPPM: TGauge;
    tbMicVolumeLevel: TTrackBar;
    Label1: TLabel;
    Label2: TLabel;
    lbType: TLabel;
    Timer1: TTimer;
    stInfo: TStaticText;
    Label3: TLabel;
    cbRate: TComboBox;
    bRecord: TButton;
    bPlay: TButton;
    bSave: TButton;
    SD: TSaveDialog;
    procedure FormCreate(Sender: TObject);
    procedure FormDestroy(Sender: TObject);
    procedure cbDevicesChange(Sender: TObject);
    procedure tbMicVolumeLevelChange(Sender: TObject);
    procedure Timer1Timer(Sender: TObject);
    procedure bRecordClick(Sender: TObject);
    procedure bPlayClick(Sender: TObject);
    procedure bSaveClick(Sender: TObject);
  private
    { Private declarations }
    WaveStream: TMemoryStream;
    procedure InitBASS;
    procedure InitWASAPI;
    procedure ListWASAPIInputDevices;
    procedure SetDefaultInputDevice;
    procedure InitInputDevice;
    procedure StartRecording;
    procedure StopRecording;
    procedure StartPlaying;
    procedure WriteToDisk;
  public
    { Public declarations }
    procedure FatalError(Msg : string);
    procedure Error(Msg: string);
    procedure BASSClose;
  end;

var
  frmWASAPImicRec: TfrmWASAPImicRec;

  indev: integer;			// current input device
  instream: HSTREAM; 	// input stream
  inmixer: HSTREAM;		// mixer for resampling input

  outdev: integer;			// output device
  outstream: HSTREAM;   // playback stream
  outmixer: HSTREAM;		// mixer for resampling output

  recbuf: array of byte; // recording buffer
  reclen: DWORD;	       // recording length

  inlevel: real;		// input level

type
  WAVHDR = packed record
    riff :			array[0..3] of AnsiChar;
    len  :			DWord;
    cWavFmt:		array[0..7] of AnsiChar;
    dwHdrLen:		DWord;
    wFormat:		Word;
    wNumChannels:	Word;
    dwSampleRate:	DWord;
    dwBytesPerSec:	DWord;
    wBlockAlign:	Word;
    wBitsPerSample:	Word;
    cData  :			array[0..3] of AnsiChar;
    dwDataLen:		DWord;
  end;
var
  WaveHdr       : WAVHDR;  // WAV header


implementation

{$R *.dfm}

// WASAPI output processing function
function OutWasapiProc(buffer: Pointer; length: DWORD; user: Pointer): DWORD; stdcall;
var
  c,d : integer;
begin
  c := BASS_ChannelGetData(outmixer, buffer, length);
  if c < 0 then begin
    d := BASS_WASAPI_GetData(nil, BASS_DATA_AVAILABLE);
    if d <= 0  then
      BASS_WASAPI_Stop(false);
    result := 0;
  end
    else result := c;
end;

// WASAPI input processing function
function InWasapiProc(buffer: Pointer; length: DWORD; user: Pointer): DWORD; stdcall;
var
  temp : array[0..49999] of byte;
  c : integer;
begin
	// give the data to the mixer feeder stream
	BASS_StreamPutData(instream, buffer, length);
  // get back resampled data from the mixer
  c := BASS_ChannelGetData(inmixer, @temp, sizeof(temp));
  while c > 0 do begin
    // Copy new buffer contents to the memory buffer
	  frmWASAPImicRec.WaveStream.Write(temp, c);
    c := BASS_ChannelGetData(inmixer, @temp, sizeof(temp));
  end;
  result := 1;
end;

{ TfrmWASAPImicRec }

procedure TfrmWASAPImicRec.BASSClose;
var
  r : boolean;
begin
  repeat
    r := BASS_WASAPI_Free;
  until not r;
  BASS_Free;
end;

procedure TfrmWASAPImicRec.bPlayClick(Sender: TObject);
begin
  StartPlaying;
end;

procedure TfrmWASAPImicRec.bRecordClick(Sender: TObject);
begin
  if inmixer = 0 then
    StartRecording
  else
    StopRecording;
end;

procedure TfrmWASAPImicRec.bSaveClick(Sender: TObject);
begin
  WriteToDisk;
end;

procedure TfrmWASAPImicRec.cbDevicesChange(Sender: TObject);
var
  i : integer;
begin
  // free current device and its mixer feeder
	BASS_WASAPI_SetDevice(indev);
	BASS_WASAPI_Free();
	BASS_StreamFree(instream);
	instream := 0;
  i := cbDevices.ItemIndex; // get the selected device
  indev := integer(cbDevices.Items.Objects[i]); // get the device #
	InitInputDevice; // initialize device
end;

procedure TfrmWASAPImicRec.Error(Msg: string);
var
	s : string;
begin
	s := Msg + #13#10 + '(Error code: ' + IntToStr(BASS_ErrorGetCode) + ')';
	MessageBox(Handle, PChar(s), nil, 0);
end;

procedure TfrmWASAPImicRec.FatalError(Msg: string);
begin
  ShowMessage('Fatal Error: '+ Msg);
  BASSClose;
  Application.Terminate;
end;

procedure TfrmWASAPImicRec.FormCreate(Sender: TObject);
begin
  indev := -1;
  outdev := -1;
  InitBASS;
  InitWASAPI;
  ListWASAPIInputDevices;
  SetDefaultInputDevice;
	WaveStream := TMemoryStream.Create;
end;

procedure TfrmWASAPImicRec.FormDestroy(Sender: TObject);
begin
	WaveStream.Free;
  BASSClose;
end;

procedure TfrmWASAPImicRec.InitBASS;
begin
  // check the correct BASS was loaded
  if (HIWORD(BASS_GetVersion) <> BASSVERSION) then
    FatalError('An incorrect version of BASS.DLL was loaded');

	// not playing anything via BASS, so don't need an update thread
	BASS_SetConfig(BASS_CONFIG_UPDATETHREADS, 0);
	// setup BASS - "no sound" device
	BASS_Init(0, 48000, 0, 0, nil);
end;

procedure TfrmWASAPImicRec.InitInputDevice;
var
	wi: BASS_WASAPI_INFO;
  level: real;
	di: BASS_WASAPI_DEVICEINFO;
  s : string;
begin
	// inialize the input device (shared mode, 1s buffer & 100ms update period)
	if (BASS_WASAPI_Init(indev, 0, 0, 0, 1, 0.1, InWasapiProc, nil)) then begin
		// create a BASS push stream of same format to feed the mixer/resampler
		BASS_WASAPI_GetInfo(wi);
		instream := BASS_StreamCreate(wi.freq, wi.chans, BASS_SAMPLE_FLOAT or BASS_STREAM_DECODE, STREAMPROC_PUSH, nil);
		if inmixer > 0 then begin // already recording, start the new device...
			BASS_Mixer_StreamAddChannel(inmixer, instream, 0);
			BASS_WASAPI_Start();
		end;
		// update level slider
		level := BASS_WASAPI_GetVolume(BASS_WASAPI_CURVE_WINDOWS);
		if level < 0 then begin // failed to get level
			level := 1; // just display 100%
      tbMicVolumeLevel.Enabled := false;
		end
    else
      tbMicVolumeLevel.Enabled := true;
  	tbMicVolumeLevel.Position := Round(level * 100);
	end
  else begin // failed, just set level slider to 0
		tbMicVolumeLevel.Position := 0;
    tbMicVolumeLevel.Enabled := false;
	end;
	// update device type display
	BASS_WASAPI_GetDeviceInfo(indev, di);
	case di.type_ of
		BASS_WASAPI_TYPE_NETWORKDEVICE:
			s := 'Remote Network Device';
		BASS_WASAPI_TYPE_SPEAKERS:
			s := 'Speakers';
		BASS_WASAPI_TYPE_LINELEVEL:
			s := 'Line In';
		BASS_WASAPI_TYPE_HEADPHONES:
			s := 'Headphones';
		BASS_WASAPI_TYPE_MICROPHONE:
			s := 'Microphone';
		BASS_WASAPI_TYPE_HEADSET:
			s := 'Headset';
		BASS_WASAPI_TYPE_HANDSET:
			s := 'Handset';
		BASS_WASAPI_TYPE_DIGITAL:
			s := 'Digital';
		BASS_WASAPI_TYPE_SPDIF:
			s := 'SPDIF';
		BASS_WASAPI_TYPE_HDMI:
			s := 'HDMI';
  else
		s := 'undefined';
  end;
	if di.flags and BASS_DEVICE_LOOPBACK = BASS_DEVICE_LOOPBACK then
    s := s + ' (loopback)';
  lbType.Caption := s;
end;

procedure TfrmWASAPImicRec.InitWASAPI;
var
  wi : BASS_WASAPI_INFO;
begin
	// initialize default WASAPI output device for playback
	if BASS_WASAPI_Init(-1, 0, 0, 0, 0.4, 0.05, OutWasapiProc, nil) then begin
		outdev := BASS_WASAPI_GetDevice();
		// create a mixer to feed the output device
		BASS_WASAPI_GetInfo(wi);
		outmixer := BASS_Mixer_StreamCreate(wi.freq, wi.chans, BASS_SAMPLE_FLOAT or BASS_STREAM_DECODE or BASS_MIXER_END or BASS_MIXER_POSEX);
	end;
end;

procedure TfrmWASAPImicRec.ListWASAPIInputDevices;
var
  c,i : integer;
  s : string;
	di : BASS_WASAPI_DEVICEINFO;
begin
  cbDevices.Clear;
  // get list of WASAPI input devices
	c := 0;
  while BASS_WASAPI_GetDeviceInfo(c, di)  do begin
    if (di.flags and BASS_DEVICE_INPUT = BASS_DEVICE_INPUT)
    and (di.flags and BASS_DEVICE_ENABLED = BASS_DEVICE_ENABLED) then begin
      s := String(di.name);
      cbDevices.Items.AddObject(s,TObject(c));
    end;
    Inc(c);
  end;
end;

procedure TfrmWASAPImicRec.SetDefaultInputDevice;
var
  i,c : integer;
	di : BASS_WASAPI_DEVICEINFO;
begin
  for i := 0 to cbDevices.Items.Count-1 do begin
    c := integer(cbDevices.Items.Objects[i]);
    BASS_WASAPI_GetDeviceInfo(c, di);
    if (di.flags and BASS_DEVICE_DEFAULT = BASS_DEVICE_DEFAULT) then begin
      indev := c;
      cbDevices.ItemIndex := i;
      InitInputDevice;
      exit;
    end;
  end;
  if indev = -1 then
    FatalError('Can''t find any WASAP inpute device');
end;

procedure TfrmWASAPImicRec.StartPlaying;
begin
	BASS_WASAPI_SetDevice(outdev);
	BASS_WASAPI_Stop(true); // flush the output device buffer (in case there is anything there)
	BASS_Mixer_ChannelSetPosition(outstream, 0, BASS_POS_BYTE); // rewind output stream
	BASS_ChannelSetPosition(outmixer, 0, BASS_POS_BYTE); // reset mixer
	BASS_WASAPI_Start; // start the device
end;

procedure TfrmWASAPImicRec.StartRecording;
var
	rate: integer;
  s : string;
begin
	if WaveStream.Size > 0 then begin // free old recording...
		BASS_StreamFree(outstream);
		outstream := 0;
		WaveStream.Clear;
    bPlay.Enabled := false;
    bSave.Enabled := false;
	end;
	// get the sample rate choice
	s := cbRate.Text;
	rate := StrToInt(s);
	reclen := 44;
	// fill the WAVE header
	with WaveHdr do begin
		riff := 'RIFF';
		len := 36;
		cWavFmt := 'WAVEfmt ';
		dwHdrLen := 16;
		wFormat := 1;
		wNumChannels := 2;
		dwSampleRate := rate;
		wBitsPerSample := 16;
		wBlockAlign := wNumChannels * wBitsPerSample div 8;
		dwBytesPerSec := dwSampleRate * wBlockAlign;
		cData := 'data';
		dwDataLen := 0;
  end;
	WaveStream.Write(WaveHdr, SizeOf(WAVHDR));

	// create a mixer and add the device's feeder stream to it
	inmixer := BASS_Mixer_StreamCreate(rate, 2, BASS_STREAM_DECODE);
	BASS_Mixer_StreamAddChannel(inmixer, instream, 0);
	// start the input device
	if (not BASS_WASAPI_SetDevice(indev)) or (not BASS_WASAPI_Start) then begin
		Error('Can''t start recording');
		BASS_StreamFree(inmixer);
		inmixer := 0;
		WaveStream.Clear;;
		exit;
	end;
  bRecord.Caption := 'Stop';
	cbRate.Enabled := false;
end;

procedure TfrmWASAPImicRec.StopRecording;
var
	i: integer;
begin
	// stop the device and free the mixer
	BASS_WASAPI_SetDevice(indev);
	BASS_WASAPI_Stop(true);
	BASS_StreamFree(inmixer);
	inmixer := 0;
  bRecord.Caption := 'Record';
	// complete the WAVE header
	WaveStream.Position := 4;
	i := WaveStream.Size - 8;
	WaveStream.Write(i, 4);
	i := i - $24;
	WaveStream.Position := 40;
	WaveStream.Write(i, 4);
	WaveStream.Position := 0;
	// enable "save" button
  bSave.Enabled := true;
	// re-enable rate selection
  cbRate.Enabled := true;
	if outdev > 0 then begin
		// create a stream from the recording
{    outstream := BASS_StreamCreateFile(BASS_FILE_MEM, recbuf, 0, reclen, BASS_SAMPLE_FLOAT or BASS_STREAM_DECODE);  }
    outstream := BASS_StreamCreateFile(True, WaveStream.Memory, 0, WaveStream.Size, BASS_STREAM_DECODE);
		if outstream > 0 then begin
			if not BASS_Mixer_StreamAddChannel(outmixer, outstream, 0) then begin
        Error('Can''t add outstream to outmixer');
        exit;
      end;
      bPlay.Enabled := true;
		end;
	end;
end;

procedure TfrmWASAPImicRec.tbMicVolumeLevelChange(Sender: TObject);
var
  level: real;
begin
	level := tbMicVolumeLevel.Position / 100;
	if BASS_WASAPI_SetDevice(indev) then
		BASS_WASAPI_SetVolume(BASS_WASAPI_CURVE_WINDOWS, level);
end;

procedure TfrmWASAPImicRec.Timer1Timer(Sender: TObject);
var
  s : string;
  pos,len : QWORD;
  delay : DWORD;
  level : real;
begin
	// update the recording/playback counter
	if outstream > 0 then begin
		BASS_WASAPI_SetDevice(outdev);
		if BASS_WASAPI_IsStarted then begin // playing
			BASS_WASAPI_Lock(true); // prevent processing mid-calculation
			delay := BASS_WASAPI_GetData(nil, BASS_DATA_AVAILABLE); // get amount of buffered data
			pos := BASS_Mixer_ChannelGetPositionEx(outstream, BASS_POS_BYTE, delay); // get source position at that point
			BASS_WASAPI_Lock(false);
      len := BASS_ChannelGetLength(outstream, BASS_POS_BYTE);
      stInfo.Caption := IntToStr(pos) + ' / ' + IntToStr(len);
    end
    else begin
      len := BASS_ChannelGetLength(outstream, BASS_POS_BYTE);
      stInfo.Caption := IntToStr(len);
    end;
  end
	else if inmixer > 0 then begin // recording
    pos := BASS_ChannelGetPosition(inmixer, BASS_POS_BYTE);
    stInfo.Caption := IntToStr(pos);
  end;

	// update the input level meter
	level := BASS_WASAPI_GetDeviceLevel(indev, -1);
  if inlevel > 0.1 then
    inlevel := inlevel - 0.1
  else
    inlevel := 0;
	if level > inlevel then
    inlevel := level;
  gaMicPPM.Progress := Round(inlevel * 100);
end;

procedure TfrmWASAPImicRec.WriteToDisk;
begin
  if not SD.Execute then exit;
  WaveStream.SaveToFile(SD.FileName);
end;

end.
