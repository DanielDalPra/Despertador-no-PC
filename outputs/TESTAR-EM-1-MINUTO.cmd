@echo off
title Teste do despertador - 1 minuto
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0despertador.ps1" -Test -TestMinutes 1
if errorlevel 1 pause
