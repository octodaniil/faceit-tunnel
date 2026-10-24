<#
  Сборка установщика Faceit Tunnel.
    .\build.ps1 -Version 1.0.1
  Результат — app\Releases: FaceitTunnel-win-Setup.exe и файлы для автообновления.
  Требуется: .NET 8 SDK и Velopack CLI (dotnet tool install -g vpk).
#>
param(
    [Parameter(Mandatory)] [string]$Version
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$App     = Join-Path $PSScriptRoot 'FaceitTunnel'
$Publish = Join-Path $PSScriptRoot 'publish'
$Out     = Join-Path $PSScriptRoot 'Releases'
$Bin     = Join-Path $PSScriptRoot '..\bin'
$SbVersion = '1.14.4'

# sing-box кладётся в установщик — скачиваем, если его ещё нет.
if (-not (Test-Path (Join-Path $Bin 'sing-box.exe'))) {
    Write-Host "Скачиваю sing-box $SbVersion..."
    $zip = Join-Path $env:TEMP "sing-box-$SbVersion.zip"
    $tmp = Join-Path $env:TEMP "sing-box-$SbVersion"
    Invoke-WebRequest "https://github.com/SagerNet/sing-box/releases/download/v$SbVersion/sing-box-$SbVersion-windows-amd64.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $tmp -Force
    New-Item -ItemType Directory -Force $Bin | Out-Null
    Get-ChildItem $tmp -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' } | Copy-Item -Destination $Bin -Force
    Remove-Item $zip, $tmp -Recurse -Force
}

if (Test-Path $Publish) { Remove-Item $Publish -Recurse -Force }
dotnet publish $App -c Release -r win-x64 --self-contained -p:Version=$Version -o $Publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }

vpk pack --packId FaceitTunnel --runtime win-x64 --packVersion $Version --packDir $Publish --mainExe FaceitTunnel.exe `
    --packTitle 'Faceit Tunnel' --packAuthors octodaniil --icon (Join-Path $App 'Assets\app.ico') --outputDir $Out
if ($LASTEXITCODE -ne 0) { throw 'vpk pack завершился с ошибкой' }

Write-Host "Готово: $Out" -ForegroundColor Green
