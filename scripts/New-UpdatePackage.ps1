param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [string]$OutputDirectory,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SigningCertificateThumbprint,
    [switch]$RequireSigning
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ($RequireSigning -and !$SigningCertificateThumbprint) { throw 'A trusted signing certificate thumbprint is required for publication.' }
if ($SigningCertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningCertificateThumbprint" -ErrorAction Stop
    if (!$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'Signing certificate is expired or has no private key.' }
}
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
    if ($env:GITHUB_ACTIONS -eq 'true') {
        dotnet run --project tests/Localization/Localization.csproj -c Debug -- --headless
    } else {
        dotnet run --project tests/Localization/Localization.csproj -c Debug
    }
    if ($LASTEXITCODE) { throw 'Localization verification failed.' }
    dotnet run --project tests/VisualStudioSelection/VisualStudioSelection.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Visual Studio selection verification failed.' }
    dotnet publish src/DevWorkspace.csproj -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true "-p:Version=$Version" -o "$output/publish"
    if ($LASTEXITCODE) { throw 'Debug publish failed.' }
    dotnet run --project tests/WorkspaceSharing/WorkspaceSharing.csproj -c Debug
    if ($LASTEXITCODE) { throw 'Workspace sharing verification failed.' }
    $packArgs = @('pack', '--packId', 'DevWorkspace', '--packVersion', $Version, '--packDir', "$output/publish", '--mainExe', 'DevWorkspace.exe', '--packTitle', 'DevWorkspace', '--packAuthors', 'developersenturk', '--channel', 'win-x64', '--runtime', 'win-x64', '--icon', 'src/Assets/DevWorkspace.ico', '--releaseNotes', 'src/Updates/ReleaseNotes.en.md', '--outputDir', "$output/releases")
    if ($SigningCertificateThumbprint) { $packArgs += @('--signParams', "/sha1 $SigningCertificateThumbprint /s My /fd sha256 /td sha256 /tr http://timestamp.digicert.com") }
    dotnet tool run vpk @packArgs
    if ($LASTEXITCODE) { throw 'Velopack packaging failed.' }
    if ($SigningCertificateThumbprint) {
        $portable = "$output/releases/DevWorkspace-win-x64-Portable.zip"
        $appToVerify = Join-Path $output 'verify-signed-DevWorkspace.exe'
        $archive = [IO.Compression.ZipFile]::OpenRead($portable)
        try {
            $entries = @($archive.Entries | Where-Object { $_.Name -eq 'DevWorkspace.exe' })
            if ($entries.Count -ne 1) { throw 'Expected exactly one DevWorkspace.exe in the portable package.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $appToVerify, $false)
        } finally { $archive.Dispose() }
        try {
        foreach ($file in @($appToVerify, "$output/releases/DevWorkspace-win-x64-Setup.exe")) {
            $signature = Get-AuthenticodeSignature -LiteralPath $file
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SigningCertificateThumbprint -or !$signature.TimeStamperCertificate) { throw "Signature or timestamp verification failed: $file" }
        }
        } finally { if (Test-Path -LiteralPath $appToVerify) { Remove-Item -LiteralPath $appToVerify } }
    }
    Write-Host "Update packages: $output/releases"
} finally { Pop-Location }
