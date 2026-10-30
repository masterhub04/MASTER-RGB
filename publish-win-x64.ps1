$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "MASTER_RGB.csproj"
$tempOut = Join-Path $PSScriptRoot ".publish-temp-win-x64"
$exeName = "MASTER RGB.exe"
$finalExe = Join-Path $PSScriptRoot $exeName

if (-not (Test-Path $project)) {
    throw "MASTER_RGB.csproj não encontrado. Coloque este script na pasta principal do projeto."
}

if (Test-Path $tempOut) { Remove-Item $tempOut -Recurse -Force }
New-Item -ItemType Directory -Path $tempOut | Out-Null

Write-Host "Restaurando dependências..." -ForegroundColor Cyan
dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore falhou." }

Write-Host "Publicando MASTER RGB para Windows x64 (self-contained / single-file)..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:AssemblyName="MASTER RGB" `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $tempOut
if ($LASTEXITCODE -ne 0) {
    Remove-Item $tempOut -Recurse -Force -ErrorAction SilentlyContinue
    throw "dotnet publish falhou. O executável anterior foi preservado."
}

$builtExe = Join-Path $tempOut $exeName
if (-not (Test-Path $builtExe)) {
    Remove-Item $tempOut -Recurse -Force -ErrorAction SilentlyContinue
    throw "A publicação terminou, mas o arquivo '$exeName' não foi encontrado."
}

Copy-Item $builtExe $finalExe -Force
Remove-Item $tempOut -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "" 
Write-Host "SUCESSO! Executável gerado na pasta principal:" -ForegroundColor Green
Write-Host $finalExe -ForegroundColor Green
