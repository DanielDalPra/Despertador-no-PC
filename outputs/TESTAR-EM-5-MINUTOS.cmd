@echo off
title Teste do despertador - 5 minutos
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0despertador.ps1" -Test
if errorlevel 1 pause
