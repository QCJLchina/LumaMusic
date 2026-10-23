Attribute VB_Name = "BASSALAC"
' BASSALAC 2.4 Visual Basic module
' Copyright (c) 2016-2025 Un4seen Developments Ltd.
'
' See the BASSALAC.CHM file for more detailed documentation

' BASS_CHANNELINFO type
Global Const BASS_CTYPE_STREAM_ALAC = &H10E00

Declare Function BASS_ALAC_StreamCreateFile64 Lib "bassalac.dll" Alias "BASS_ALAC_StreamCreateFile" (ByVal filetype As Long, ByVal file As Any, ByVal offset As Long, ByVal offsethigh As Long, ByVal length As Long, ByVal lengthhigh As Long, ByVal flags As Long) As Long
Declare Function BASS_ALAC_StreamCreateURL Lib "bassalac.dll" (ByVal url As String, ByVal offset As Long, ByVal flags As Long, ByVal proc As Long, ByVal user As Long) As Long
Declare Function BASS_ALAC_StreamCreateFileUser Lib "bassalac.dll" (ByVal system As Long, ByVal flags As Long, ByVal procs As Long, ByVal user As Long) As Long

' 32-bit wrappers for 64-bit BASS functions
Function BASS_ALAC_StreamCreateFile(ByVal filetype As Long, ByVal file As Long, ByVal offset As Long, ByVal length As Long, ByVal flags As Long) As Long
BASS_ALAC_StreamCreateFile = BASS_ALAC_StreamCreateFile64(filetype, file, offset, 0, length, 0, flags Or BASS_UNICODE)
End Function
