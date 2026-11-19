# Contributing to DevWorkspace

Bug reports, project detection fixtures, translations, documentation and focused pull requests are welcome. Search existing issues first. Explain the problem and expected behavior; use a small anonymized example when possible.

## Build and validate

Use Windows x64 with the .NET 10 SDK. Building requires access to the commercial DevExpress WPF packages and an appropriate DevExpress license. Our MIT license applies to the original DevWorkspace code, not third-party components. See [third-party notices](THIRD-PARTY-NOTICES.md).

```powershell
dotnet restore DevWorkspace.slnx
dotnet build DevWorkspace.slnx -c Debug
dotnet run --project tests/ProjectDetection -c Debug
dotnet run --project tests/Localization -c Debug -- --headless
dotnet run --project tests/WorkspaceSharing -c Debug
```

Run the relevant checks under `tests/` for your changes. Use Debug for all validation and packaging. Do not create Release packages. Keep changes focused, use the existing theme resources, provide keyboard access, and add matching English and Turkish localization keys. Add user-facing changes to both `src/Updates/ReleaseNotes.en.md` and `src/Updates/ReleaseNotes.tr.md` for a new version.

## Pull requests

Describe the user-visible change, why it helps and how you checked it. Include screenshots for UI changes. Add meaningful regression checks for behavior changes. Do not include credentials, personal paths, sample repositories with real customer data, build outputs, or private DevExpress feed credentials.

By submitting a contribution, you agree to license your original contribution under the project's [MIT license](LICENSE). Only contribute work you have permission to license. No separate CLA is required.

## Security reports

Do not disclose credentials or exploitable private details in public issues. Use the repository's GitHub **Security → Report a vulnerability** option if enabled; otherwise contact the maintainer through their GitHub profile to arrange a private channel before sharing details.
