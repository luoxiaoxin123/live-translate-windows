# Builds the distributable installer (and an optional portable zip):
#   LiveTranslate-Setup-x64.exe   ← what you attach to a GitHub Release
#   LiveTranslate-win-x64.zip     ← portable fallback (extract + 实时翻译.exe)
# Run from the repo root:  pwsh -File tools\pack.ps1

param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $env:APP_VERSION }
if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionNode = Select-Xml -Path 'src\LiveTranslate.App\LiveTranslate.App.csproj' -XPath '//Version'
    $Version = if ($versionNode) { $versionNode.Node.InnerText.Trim() } else { '0.1.0' }
}
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+') { throw "Version must look like 0.2.0 (optional v prefix), got '$Version'" }

$numeric = if ($Version -match '^(\d+\.\d+\.\d+)') { $Matches[1] }
    elseif ($Version -match '^(\d+\.\d+)') { "$($Matches[1]).0" }
    else { $Version }

Write-Host "version $Version (assembly $numeric)"

dotnet publish src/LiveTranslate.App/LiveTranslate.App.csproj -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishReadyToRun=true `
    -p:Version=$Version -p:AssemblyVersion=$numeric -p:FileVersion=$numeric `
    -p:InformationalVersion=$Version `
    -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Get-ChildItem publish\win-x64 -Filter *.pdb -Recurse | Remove-Item -Force

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup'
}

$setupOut = Join-Path $root 'LiveTranslate-Setup-x64.exe'
if (Test-Path $setupOut) { Remove-Item $setupOut -Force }

& $iscc /Q "/DMyAppVersion=$Version" (Join-Path $PSScriptRoot 'setup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }
if (-not (Test-Path $setupOut)) { throw "installer not produced: $setupOut" }

# Portable zip: tiny launcher + app\, for people who do not want an installer.
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
New-Item -ItemType Directory dist | Out-Null

& $csc -nologo -target:winexe -codepage:65001 `
    "-win32icon:src\LiveTranslate.App\Assets\app.ico" `
    "-out:dist\实时翻译.exe" tools\launcher\launcher.cs
if ($LASTEXITCODE -ne 0) { throw 'launcher build failed' }

Copy-Item publish\win-x64 dist\app -Recurse
Copy-Item (Join-Path $PSScriptRoot '*.txt') dist\

$zip = 'LiveTranslate-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path dist\* -DestinationPath $zip -CompressionLevel Optimal

$setupMb = [math]::Round((Get-Item $setupOut).Length / 1MB)
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Host "done: LiveTranslate-Setup-x64.exe ($setupMb MB)  LiveTranslate-win-x64.zip ($zipMb MB)"
