# Builds the distributable zip:
#   LiveTranslate-win-x64.zip
#   ├─ 实时翻译.exe   ← tiny launcher (the obvious thing to double-click)
#   ├─ 使用说明.txt
#   └─ app\           ← self-contained publish output
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools\pack.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish src/LiveTranslate.App/LiveTranslate.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
New-Item -ItemType Directory dist | Out-Null

& $csc -nologo -target:winexe -codepage:65001 `
    "-win32icon:src\LiveTranslate.App\Assets\app.ico" `
    "-out:dist\实时翻译.exe" tools\launcher\launcher.cs
if ($LASTEXITCODE -ne 0) { throw 'launcher build failed' }

Copy-Item publish\win-x64 dist\app -Recurse
Copy-Item tools\使用说明.txt dist\app\

# Compress-Archive (not tar): it stores non-ASCII entry names with the UTF-8 flag,
# so 实时翻译.exe extracts correctly on any system locale.
$zip = 'LiveTranslate-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path dist\* -DestinationPath $zip -CompressionLevel Optimal

Write-Host "done: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
