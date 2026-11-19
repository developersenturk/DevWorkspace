param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
if ((Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') { throw 'WinGet submission requires a valid signed installer.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
$url = "https://github.com/developersenturk/DevWorkspace/releases/download/v$Version/DevWorkspace-win-x64-Setup.exe"
$download = Join-Path ([IO.Path]::GetTempPath()) ("devworkspace-winget-" + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    Invoke-WebRequest -Uri $url -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $hash) { throw 'Published installer does not match the local installer.' }
} finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download } }
if (!$OutputDirectory) { $OutputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) ".artifacts/winget/$Version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$manifest = Join-Path $output 'developersenturk.DevWorkspace.yaml'
if (Test-Path -LiteralPath $manifest) { throw "Manifest already exists: $manifest" }
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.singleton.1.12.0.schema.json
PackageIdentifier: developersenturk.DevWorkspace
PackageVersion: $Version
PackageLocale: en-US
Publisher: developersenturk
PublisherUrl: https://github.com/developersenturk
PackageName: DevWorkspace
PackageUrl: https://github.com/developersenturk/DevWorkspace
License: MIT
LicenseUrl: https://github.com/developersenturk/DevWorkspace/blob/v$Version/LICENSE
ShortDescription: A Windows workbench for projects, Git and development tools.
Installers:
  - Architecture: x64
    InstallerType: exe
    Scope: user
    InstallerUrl: $url
    InstallerSha256: $hash
    InstallerSwitches:
      Silent: --silent
      SilentWithProgress: --silent
    AppsAndFeaturesEntries:
      - DisplayName: DevWorkspace
        ProductCode: DevWorkspace
ManifestType: singleton
ManifestVersion: 1.12.0
"@ | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
if (Get-Command winget -ErrorAction SilentlyContinue) {
    winget validate --manifest $output
    if ($LASTEXITCODE) { throw 'WinGet manifest validation failed.' }
} else { Write-Warning 'WinGet is unavailable; run winget validate on Windows before submitting.' }
Write-Host "Manifest prepared for review: $manifest"
