# Signing and WinGet distribution

These integrations prepare distribution; the repository is not automatically registered in WinGet. A trusted signing identity and a published stable installer are external prerequisites. Local builds stay in Debug.

## Code signing

`scripts/New-UpdatePackage.ps1` accepts `-SigningCertificateThumbprint` and `-RequireSigning`. Use a trusted code-signing certificate with its private key available in the current user's Windows certificate store, plus the Windows SDK signing tools. The script uses Velopack's signing support while packaging, SHA-256 file and timestamp digests, and an RFC 3161 timestamp service. It verifies the app and generated installer signatures after packaging. It fails when required signing is unavailable; it never creates a self-signed certificate or claims unsigned builds are trusted.

```powershell
./scripts/New-UpdatePackage.ps1 -Version 1.1.0 -SigningCertificateThumbprint YOUR_40_HEX_THUMBPRINT -RequireSigning
```

An unsigned local package is still possible without these switches. Publishing from the update workflow requires signing. Configure the protected runner's certificate before invoking that workflow and set repository variable `WINDOWS_SIGNING_THUMBPRINT`. GitHub-hosted runners do not inherit your local certificate store; provision the certificate securely or use a runner with a hardware-backed signing identity. Do not commit a certificate, private key, password or signing token. Existing advanced `VPK_SIGN_PARAMS` / cloud-signing configurations can be used locally; the thumbprint route is the supported required-signing path in this script.

See [Velopack code signing](https://docs.velopack.io/packaging/signing) and the [Windows CLI reference](https://docs.velopack.io/reference/cli/content/vpk-windows).

## WinGet

After a signed stable installer is published to GitHub Releases:

```powershell
./scripts/New-WinGetManifest.ps1 -Version 1.1.0 -InstallerPath .artifacts/update/releases/DevWorkspace-win-x64-Setup.exe
```

The generator downloads the version-specific public release asset, checks it against the local installer's SHA-256, requires a valid Authenticode signature and emits a versioned singleton manifest. It uses Velopack's `--silent` switch for unattended installs and the installed-app `DevWorkspace` registration for upgrades. If WinGet is installed, it also runs `winget validate`. No placeholder hashes or unverified URLs are emitted.

Test install, upgrade and uninstall on a clean Windows VM before submitting a PR to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). First-package submission is a separate review process. Do not advertise `winget install developersenturk.DevWorkspace` until the manifest has been accepted and appears in the public source.

References: [Microsoft's manifest guide](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest), [Velopack setup switches](https://docs.velopack.io/reference/cli/content/setup-windows).
