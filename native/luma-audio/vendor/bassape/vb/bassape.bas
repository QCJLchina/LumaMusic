Attribute VB_Name = "BASSAPE"
' BASSAPE 2.4 Visual Basic module
' Copyright (c) 2022-2025 Un4seen Developments Ltd.
'
' See the BASSAPE.CHM file for more detailed documentation

' BASS_CHANNELINFO type
Global Const BASS_CTYPE_STREAM_APE = &H10700

Declare Function BASS_APE_StreamCreateFile64 Lib "bassape.dll" Alias "BASS_APE_StreamCreateFile" (ByVal filetype As Long, ByVal file As Any, ByVal offset As Long, ByVal offsethigh As Long, ByVal length As Long, ByVal lengthhigh As Long, ByVal flags As Long) As Long
Declare Function BASS_APE_StreamCreateURL Lib "bassape.dll" (ByVal url As String, ByVal offset As Long, ByVal flags As Long, ByVal proc As Long, ByVal user As Long) As Long
Declare Function BASS_APE_StreamCreateFileUser Lib "bassape.dll" (ByVal system As Long, ByVal flags As Long, ByVal procs As Long, ByVal user As Long) As Long

' 32-bit wrappers for 64-bit BASS functions
Function BASS_APE_StreamCreateFile(ByVal filetype As Long, ByVal file As Long, ByVal offset As Long, ByVal length As Long, ByVal flags As Long) As Long
BASS_APE_StreamCreateFile = BASS_APE_StreamCreateFile64(filetype, file, offset, 0, length, 0, flags Or BASS_UNICODE)
End Function
