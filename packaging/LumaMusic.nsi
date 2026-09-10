; LumaMusic NSIS 安装器（用户级安装，不弹 UAC）。
; 行为：结束运行中的应用 → 覆盖安装 → 开始菜单 + 标准卸载项；卸载保留 %LOCALAPPDATA%\LumaMusic 用户数据。
; 编码坑：脚本里的中文默认按系统 ANSI 码页解析，中文会变乱码（快捷方式名直接烂掉）；
;         .nsi 必须存 UTF-8 无 BOM，并让 scripts/make-installer.ps1 传 /INPUTCHARSET UTF8。
; 版本号由 make-installer.ps1 以 /DVERSION=x.y.z 注入，默认值只给直接调 makensis 时兜底。

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"

!ifndef VERSION
  !define VERSION "1.1.4"
!endif

!define APPNAME "Luma Music"
!define APPEXE  "LumaMusic.exe"
!define DSDEXE  "LumaDsd.exe"
!define COMPANY "Luma"
; 本安装器的落款标记：只有确认目录由本安装器创建过，覆盖安装前才清空 $INSTDIR，避免误删用户自己的目录
!define MARKER  ".luma-nsis-install"
!define REGKEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\LumaMusic"

Name "${APPNAME}"
OutFile "LumaMusic-Setup.exe"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\LumaMusic"
InstallDirRegKey HKCU "Software\LumaMusic" "InstallDir"
SetCompressor /SOLID lzma
SetCompressorDictSize 64

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} 安装程序"
VIAddVersionKey "FileVersion" "${VERSION}.0"
VIAddVersionKey "ProductVersion" "${VERSION}.0"
VIAddVersionKey "LegalCopyright" "${COMPANY}"

!define MUI_ICON "..\app\Assets\Luma.ico"
!define MUI_UNICON "..\app\Assets\Luma.ico"
!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "运行 ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; 等应用自己退出（最多 5 秒）：升级来自应用内时它会先写回播放状态再关闭，
; 直接 taskkill 会丢状态。
; 判断方式：CSV 输出里命中进程的行一定以引号开头；没命中时 tasklist 打印的是本地化的
; 提示文字（中文/英文都不以引号开头）——据此立刻跳出，应用没在运行时不必白等 5 秒。
Function WaitForAppExit
  StrCpy $1 0
  wfe_loop:
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${APPEXE}" /NH /FO CSV'
    Pop $0
    Pop $2
    StrCpy $3 $2 1
    StrCmp $3 '"' 0 wfe_done
    IntOp $1 $1 + 1
    IntCmp $1 20 wfe_done
    Sleep 250
    Goto wfe_loop
  wfe_done:
FunctionEnd

; 兜底强杀：DSD 解码工作进程也要退，否则覆盖 LumaDsd.exe 时文件被占用
Function CloseRunning
  nsExec::ExecToLog 'taskkill /IM ${APPEXE} /F'
  Pop $0
  nsExec::ExecToLog 'taskkill /IM ${DSDEXE} /F'
  Pop $0
FunctionEnd

; 卸载段只能用 un. 前缀的函数，逻辑与上面两个安装段函数一致。
Function un.WaitForAppExit
  StrCpy $1 0
  un_wfe_loop:
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${APPEXE}" /NH /FO CSV'
    Pop $0
    Pop $2
    StrCpy $3 $2 1
    StrCmp $3 '"' 0 un_wfe_done
    IntOp $1 $1 + 1
    IntCmp $1 20 un_wfe_done
    Sleep 250
    Goto un_wfe_loop
  un_wfe_done:
FunctionEnd

Function un.CloseRunning
  nsExec::ExecToLog 'taskkill /IM ${APPEXE} /F'
  Pop $0
  nsExec::ExecToLog 'taskkill /IM ${DSDEXE} /F'
  Pop $0
FunctionEnd

Section "Install"
  Call WaitForAppExit
  Call CloseRunning

  IfFileExists "$INSTDIR\${MARKER}" 0 noWipe
    RMDir /r "$INSTDIR"
  noWipe:

  SetOutPath "$INSTDIR"
  File /r "..\dist\LumaMusic\*.*"
  FileOpen $0 "$INSTDIR\${MARKER}" w
  FileWrite $0 "LumaMusic NSIS"
  FileClose $0

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\Luma Music"
  CreateShortCut "$SMPROGRAMS\Luma Music\Luma Music.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
  CreateShortCut "$SMPROGRAMS\Luma Music\卸载 Luma Music.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\LumaMusic" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\${APPEXE}"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "${REGKEY}" "QuietUninstallString" "$\"$INSTDIR\Uninstall.exe$\" /S"
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "Publisher" "${COMPANY}"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${REGKEY}" "EstimatedSize" "$0"

  ; 应用内升级时由应用传 /RELAUNCH=1：装完重新拉起，用户感觉是原地升级而非被关闭。
  ; 坑：FileFunc 的 GetOptions 搜索串必须带上 "="，写 "RELAUNCH" 只会取到 "=1"（实测）。
  ${GetOptions} $CMDLINE "RELAUNCH=" $1
  StrCmp $1 "1" 0 noRelaunch
    ; 覆盖运行中的应用时，NSIS 先把文件写进临时目录、等本进程退出前才搬进 $INSTDIR，
    ; 此刻直接 Exec 目标还不存在。交给 cmd 等几秒（应用文件被占用时搬完约需 1-2 秒）再拉起。
    Exec 'cmd.exe /c ping -n 4 127.0.0.1 > nul & start "" "$INSTDIR\${APPEXE}"'
  noRelaunch:
SectionEnd

Section "Uninstall"
  Call un.WaitForAppExit
  Call un.CloseRunning

  Delete "$SMPROGRAMS\Luma Music\Luma Music.lnk"
  Delete "$SMPROGRAMS\Luma Music\卸载 Luma Music.lnk"
  RMDir "$SMPROGRAMS\Luma Music"

  ; 只删安装目录：用户曲库与设置留在 %LOCALAPPDATA%\LumaMusic，与 MSIX 版行为一致
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${REGKEY}"
  DeleteRegKey HKCU "Software\LumaMusic"
SectionEnd
