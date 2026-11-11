; Tauri provides COM.nsh and owns install/update handling. Keep its template intact.
; Tell Shell which installed items changed, including /UPDATE where shortcuts
; are deliberately retained. Do not flush unrelated icons or restart Explorer.
!macro NSIS_HOOK_POSTINSTALL
  Push $0
  System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x00001005, w "$INSTDIR\${MAINBINARYNAME}.exe", p 0)'

  !insertmacro IsShortcutTarget "$SMPROGRAMS\$AppStartMenuFolder\${PRODUCTNAME}.lnk" "$INSTDIR\${MAINBINARYNAME}.exe"
  Pop $0
  ${If} $0 = 1
    System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x00001005, w "$SMPROGRAMS\$AppStartMenuFolder\${PRODUCTNAME}.lnk", p 0)'
  ${EndIf}
  !insertmacro IsShortcutTarget "$SMPROGRAMS\${PRODUCTNAME}.lnk" "$INSTDIR\${MAINBINARYNAME}.exe"
  Pop $0
  ${If} $0 = 1
    System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x00001005, w "$SMPROGRAMS\${PRODUCTNAME}.lnk", p 0)'
  ${EndIf}
  !insertmacro IsShortcutTarget "$DESKTOP\${PRODUCTNAME}.lnk" "$INSTDIR\${MAINBINARYNAME}.exe"
  Pop $0
  ${If} $0 = 1
    System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x00001005, w "$DESKTOP\${PRODUCTNAME}.lnk", p 0)'
  ${EndIf}
  Pop $0
!macroend

; Only remove our exact startup entry on a real uninstall, never during /UPDATE.
!macro NSIS_HOOK_POSTUNINSTALL
  ${If} $UpdateMode <> 1
    Push $0
    Push $1
    Push $2
    Push $3
    !insertmacro ComHlpr_CreateInProcInstance ${CLSID_ShellLink} ${IID_IShellLink} r0 ""
    ${If} $0 P<> 0
      ${IUnknown::QueryInterface} $0 '("${IID_IPersistFile}", .r1)'
      ${If} $1 P<> 0
        ${IPersistFile::Load} $1 '("$SMSTARTUP\ALTER GENESIS (Tauri).lnk", ${STGM_READ}).r3'
        ${If} $3 = 0
          ${IShellLink::GetPath} $0 '(.r2, ${NSIS_MAX_STRLEN}, 0, ${SLGP_RAWPATH}).r3'
          ${If} $3 = 0
          ${AndIf} $2 == "$INSTDIR\${MAINBINARYNAME}.exe"
            ${IShellLink::GetArguments} $0 '(.r2, ${NSIS_MAX_STRLEN}).r3'
            ${If} $3 = 0
            ${AndIf} $2 == "--startup"
              Delete "$SMSTARTUP\ALTER GENESIS (Tauri).lnk"
            ${EndIf}
          ${EndIf}
        ${EndIf}
        ${IUnknown::Release} $1 ""
      ${EndIf}
      ${IUnknown::Release} $0 ""
    ${EndIf}
    Pop $3
    Pop $2
    Pop $1
    Pop $0
  ${EndIf}
!macroend
