param(
    [string]$AntigravityPath = (Join-Path $env:LOCALAPPDATA 'Programs\antigravity\Antigravity.exe')
)
$ErrorActionPreference = 'Stop'
$iconPath = Join-Path $PSScriptRoot 'antigravity.ico'
if (-not (Test-Path -LiteralPath $iconPath)) {
    & (Join-Path $PSScriptRoot 'extract-icon.ps1') -ApplicationPath $AntigravityPath
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'AntigravityWallpaperLauncher.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/out:$output" "/win32icon:$(Join-Path $PSScriptRoot 'antigravity.ico')" "/win32manifest:$(Join-Path $PSScriptRoot 'launcher.manifest')" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'Launcher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
Get-Item -LiteralPath $output | Select-Object FullName, Length
