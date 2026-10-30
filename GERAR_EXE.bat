@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo       MASTER RGB - GERAR EXECUTAVEL
echo ============================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-win-x64.ps1"
if errorlevel 1 (
  echo.
  echo ERRO: nao foi possivel gerar o executavel.
  pause
  exit /b 1
)
echo.
echo Executavel gerado na pasta principal:
echo %~dp0MASTER RGB.exe
echo.
pause
