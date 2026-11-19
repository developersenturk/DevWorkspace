param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ".artifacts/updates/$Version-$([Guid]::NewGuid().ToString('N').Substring(0, 8))" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Use a new output directory to avoid including stale update packages: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $repoRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Could not restore the pinned Velopack CLI.' }
    dotnet build src/DevWorkspace.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Debug build failed.' }
    dotnet run --project tests/AppUpdates/AppUpdates.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Update verification failed.' }
    dotnet run --project tests/ReleaseCenter/ReleaseCenter.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Release center verification failed.' }
    dotnet run --project tests/ProjectDetection/ProjectDetection.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Project detection verification failed.' }
    dotnet run --project tests/GitHubLinks/GitHubLinks.csproj -c Debug
    if ($LASTEXITCODE) { throw 'GitHub link verification failed.' }
    dotnet run --project tests/Localization/Localization.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Localization verification failed.' }
    dotnet run --project tests/VisualStudioSelection/VisualStudioSelection.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Visual Studio selection verification failed.' }
    dotnet publish src/DevWorkspace.csproj -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true "-p:Version=$Version" -o "$output/publish"
    if ($LASTEXITCODE) { throw 'Debug publish failed.' }
    dotnet tool run vpk pack --packId DevWorkspace --packVersion $Version --packDir "$output/publish" --mainExe DevWorkspace.exe --packTitle DevWorkspace --channel win-x64 --runtime win-x64 --icon src/Assets/DevWorkspace.ico --outputDir "$output/releases"
    if ($LASTEXITCODE) { throw 'Velopack packaging failed.' }
    Write-Host "Update packages: $output/releases"
} finally { Pop-Location }
