; UZIP2 - optional Windows installer (NSIS)
; ---------------------------------------------------------------------------
; This is a RECIPE, not a build artifact. The primary, dependency-free install
; path is scripts\install.ps1 (per-user, no admin, no external tools). Use this
; only if you specifically want a double-click setup.exe.
;
; To build (requires NSIS installed, e.g. winget install NSIS.NSIS):
;   1. Run:  powershell -File scripts\publish.ps1
;   2. Stage the framework-dependent build folder at ..\artifacts\3.4.0\fd
;      (edit VER + STAGING below to match the published version),
;      and optionally put a 7-Zip\ folder next to it.
;   3. Run:  makensis scripts\installer.nsi   ->  ..\artifacts\UZIP2-<VER>-setup.exe
;
; The context menu is registered by calling the app's own `shell register`, so
; it always points at wherever setup.exe actually installed UZIP2.exe.
; ---------------------------------------------------------------------------

!include "MUI2.nsh"

!define NAME    "UZIP2"
!define VER     "3.4.0"
!define PUBLISHER "UZIP2"
!define STAGING "..\artifacts\${VER}\fd"    ; folder holding UZIP2.exe (+ configs, optional 7-Zip\)

Name "${NAME} ${VER}"
OutFile "..\artifacts\${NAME}-${VER}-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${NAME}"
InstallDirRegKey HKCU "Software\${NAME}" "InstallDir"
RequestExecutionLevel user        ; per-user, no UAC prompt
SetCompressor /SOLID lzma

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "Install"
  SectionIn RO
  SetOutPath "$INSTDIR"
  File "${STAGING}\UZIP2.exe"
  File "${STAGING}\App.config"
  File "${STAGING}\UZIP2.dll.config"
  IfFileExists "${STAGING}\7-Zip\7z.exe" 0 +2
    File /r "${STAGING}\7-Zip\*.*"        ; bundle 7-Zip only if it was staged

  WriteRegStr HKCU "Software\${NAME}" "InstallDir" "$INSTDIR"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Start Menu shortcut
  CreateDirectory "$SMPROGRAMS\${NAME}"
  CreateShortcut "$SMPROGRAMS\${NAME}\${NAME}.lnk" "$INSTDIR\UZIP2.exe" "" "$INSTDIR\UZIP2.exe" 0
  CreateShortcut "$SMPROGRAMS\${NAME}\Uninstall.lnk" "$INSTDIR\uninstall.exe"

  ; Explorer context menu (HKCU; the app writes its own keys pointing at $INSTDIR)
  nsExec::ExecToLog '"$INSTDIR\UZIP2.exe" shell register'
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog '"$INSTDIR\UZIP2.exe" shell unregister'
  RMDir /r "$SMPROGRAMS\${NAME}"
  Delete "$INSTDIR\UZIP2.exe"
  Delete "$INSTDIR\App.config"
  Delete "$INSTDIR\UZIP2.dll.config"
  RMDir /r "$INSTDIR\7-Zip"
  ; Config\ and logs\ are the user's live data -> ask before removing them.
  MessageBox MB_YESNO|MB_ICONEXCLAMATION "Keep your passwords / history in Config\ and logs\?$\r$\n$\r$\nSelect No to delete them too." IDYES keep IDNO del
    del:
      RMDir /r "$INSTDIR\Config"
      RMDir /r "$INSTDIR\logs"
      RMDir "$INSTDIR"
      Goto end
    keep:
      MessageBox MB_OK "Config\ and logs\ were kept in$\r$\n$INSTDIR"
  end:
  DeleteRegKey HKCU "Software\${NAME}"
SectionEnd
