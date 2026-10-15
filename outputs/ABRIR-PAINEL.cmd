@echo off
cd /d "%~dp0"
if not exist "%~dp0Despertador.exe" (
  echo Compilando interface...
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0compilar-painel.ps1"
  if errorlevel 1 (
    echo Falha ao abrir. Nao ha alarmes ativados por este painel.
    pause
    exit /b 1
  )
)
start "" "%~dp0Despertador.exe"
