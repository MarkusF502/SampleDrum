param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/release'),
    [string]$Version = '0.3.0'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a numeric semantic version.' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $outputRoot) {
    if (Get-ChildItem -LiteralPath $outputRoot -Force) { throw "Output must be empty: $outputRoot" }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$pcName = "SampleDrum-v$Version-win-x64"
$mobileName = "SampleDrum-v$Version-mobile-web"
$pc = Join-Path $outputRoot $pcName
$mobile = Join-Path $outputRoot $mobileName
& dotnet publish (Join-Path $repoRoot 'src/SampleDrum.App') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $pc --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item (Join-Path $repoRoot 'README.md') $pc
Copy-Item (Join-Path $repoRoot 'docs') $pc -Recurse
Set-Content (Join-Path $pc 'Iniciar-simulacao.cmd') '@echo off
"%~dp0SampleDrum.App.exe" --simulation' -Encoding ascii
New-Item -ItemType Directory -Path (Join-Path $mobile 'assets') -Force | Out-Null
$web = Join-Path $repoRoot 'src/SampleDrum.Remote/wwwroot'
Copy-Item (Join-Path $web 'index.html') $mobile
Copy-Item (Join-Path $web 'app.js'),(Join-Path $web 'app.css'),(Join-Path $web 'icon.svg') (Join-Path $mobile 'assets')
Set-Content (Join-Path $mobile 'LEIA-ME.txt') @"
SampleDrum v$Version - painel mobile web (pt-BR)

O painel mobile ja esta embutido no pacote Windows. Nao precisa instalar este ZIP no celular.
No PC: execute SampleDrum.App.exe, ative Painel do celular na rede local e leia o QR pelo navegador do celular.
O codigo tem 8 digitos, vale 5 minutos e serve uma vez. Gere outro no PC para adicionar outro aparelho.

Este ZIP e uma copia dos arquivos web para consulta/desenvolvimento. Abrir index.html diretamente ou hospeda-lo
sozinho nao controla o audio: o painel depende da API e do cookie de pareamento do aplicativo Windows.
Android e iPhone usam o navegador; esta versao nao contem APK nem aplicativo iOS nativo.
"@ -Encoding utf8
Compress-Archive -LiteralPath $pc -DestinationPath (Join-Path $outputRoot "$pcName.zip") -CompressionLevel Optimal
Compress-Archive -LiteralPath $mobile -DestinationPath (Join-Path $outputRoot "$mobileName.zip") -CompressionLevel Optimal
Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
} | Set-Content (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Release packages: $outputRoot"
