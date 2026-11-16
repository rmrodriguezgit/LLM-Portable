@echo off
setlocal
rem ==========================================================================
rem  LocalLLM Portable - lanzador Zero-Admin
rem  Inicia el Gateway (WinExe, sin ventana de consola) y cierra esta consola.
rem  No requiere Administrador ni modifica PATH, HKLM ni Program Files.
rem ==========================================================================
cd /d "%~dp0"

if not exist "%~dp0LocalLLM-Gateway.exe" (
  echo No se encontro LocalLLM-Gateway.exe en "%~dp0".
  pause
  exit /b 1
)

rem Primer arranque: quitar la "marca de la web" (Zone.Identifier) que Windows agrega a los
rem archivos extraidos de un .zip descargado, para evitar avisos de SmartScreen en cada ejecutable.
rem Solo afecta a archivos de esta carpeta del usuario; no requiere privilegios.
if not exist "%~dp0data\.unblocked" (
  powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command ^
    "Get-ChildItem -LiteralPath '%~dp0' -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1
  if not exist "%~dp0data" mkdir "%~dp0data"
  type nul > "%~dp0data\.unblocked"
)

start "" "%~dp0LocalLLM-Gateway.exe" %*
endlocal
exit /b 0
