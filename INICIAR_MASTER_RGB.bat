@echo off
setlocal
cd /d "%~dp0"
set "PROJECT=%~dp0MASTER_RGB.csproj"

if not exist "%PROJECT%" (
  echo [ERRO] Nao encontrei MASTER_RGB.csproj nesta pasta.
  pause
  exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERRO] .NET SDK nao encontrado.
  echo Instale o .NET 8 SDK para executar o MASTER RGB.
  pause
  exit /b 1
)

echo Iniciando MASTER RGB...
dotnet run --project "%PROJECT%" --configuration Release
if errorlevel 1 (
  echo.
  echo O programa nao conseguiu iniciar. Confira a mensagem acima.
  pause
)
endlocal
