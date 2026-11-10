; Tauri provides COM.nsh and owns install/update handling. Keep its template intact.
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
