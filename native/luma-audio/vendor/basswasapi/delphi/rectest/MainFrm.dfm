object frmWASAPImicRec: TfrmWASAPImicRec
  Left = 0
  Top = 0
  Caption = 'BASSWASAPI recording test'
  ClientHeight = 163
  ClientWidth = 530
  Color = clBtnFace
  Font.Charset = DEFAULT_CHARSET
  Font.Color = clWindowText
  Font.Height = -16
  Font.Name = 'Tahoma'
  Font.Style = []
  OldCreateOrder = False
  OnCreate = FormCreate
  OnDestroy = FormDestroy
  PixelsPerInch = 96
  TextHeight = 19
  object gaMicPPM: TGauge
    Left = 503
    Top = 8
    Width = 18
    Height = 143
    ForeColor = clAqua
    Kind = gkVerticalBar
    Progress = 0
    ShowText = False
  end
  object Label1: TLabel
    Left = 256
    Top = 47
    Width = 58
    Height = 19
    Caption = 'volume:'
  end
  object Label2: TLabel
    Left = 8
    Top = 47
    Width = 36
    Height = 19
    Caption = 'type:'
  end
  object lbType: TLabel
    Left = 50
    Top = 47
    Width = 10
    Height = 19
    Caption = '  '
  end
  object Label3: TLabel
    Left = 8
    Top = 87
    Width = 33
    Height = 19
    Caption = 'rate:'
  end
  object cbDevices: TComboBox
    Left = 8
    Top = 8
    Width = 489
    Height = 27
    AutoComplete = False
    Style = csDropDownList
    TabOrder = 0
    OnChange = cbDevicesChange
  end
  object tbMicVolumeLevel: TTrackBar
    Left = 320
    Top = 48
    Width = 177
    Height = 25
    Max = 100
    ShowSelRange = False
    TabOrder = 1
    TickStyle = tsNone
    OnChange = tbMicVolumeLevelChange
  end
  object stInfo: TStaticText
    Left = 8
    Top = 125
    Width = 352
    Height = 23
    Alignment = taCenter
    AutoSize = False
    BorderStyle = sbsSunken
    TabOrder = 2
  end
  object cbRate: TComboBox
    Left = 47
    Top = 84
    Width = 82
    Height = 27
    ItemIndex = 2
    TabOrder = 3
    Text = '44100'
    Items.Strings = (
      '96000'
      '48000'
      '44100'
      '32000'
      '22050')
  end
  object bRecord: TButton
    Left = 144
    Top = 84
    Width = 105
    Height = 27
    Caption = 'Record'
    TabOrder = 4
    OnClick = bRecordClick
  end
  object bPlay: TButton
    Left = 255
    Top = 84
    Width = 105
    Height = 27
    Caption = 'Play'
    Enabled = False
    TabOrder = 5
    OnClick = bPlayClick
  end
  object bSave: TButton
    Left = 376
    Top = 123
    Width = 105
    Height = 27
    Caption = 'Save'
    Enabled = False
    TabOrder = 6
    OnClick = bSaveClick
  end
  object Timer1: TTimer
    Interval = 50
    OnTimer = Timer1Timer
    Left = 384
    Top = 74
  end
  object SD: TSaveDialog
    Filter = 'WAV file (*.wav)|*.wav|All files (*.*)|*.*'
    Left = 432
    Top = 72
  end
end
