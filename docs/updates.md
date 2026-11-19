# Application updates

DevWorkspace uses Velopack **1.2.161**, with public GitHub Releases at
`https://github.com/developersenturk/DevWorkspace` and the **win-x64** channel.
The application never embeds a GitHub token. Drafts and prereleases are excluded.

## User experience

- Installed copies check five seconds after startup, then every six hours. **Help → Check for updates** also checks immediately.
- New versions download in the background. Velopack verifies the package before the banner announces it is ready.
- A ready update stays staged while you work. Starting the application again applies it before the workbench opens.
- **Restart now** saves the workspace and layout through the normal closing path, then launches the updater after the window has closed.
- Restart is blocked while workspace/Git operations are running. Other running copies from the same installation hold shared leases, so an update cannot replace their files.
- Network and package errors leave the current application usable; automatic checks retry later, and a manual check offers a retry.
- Preferences remain in `%LocalAppData%/DevWorkspace`, outside the application directory Velopack replaces.

The raw `DevWorkspace.exe` copied to the repository root is a standalone development build. It cannot gain a Velopack installation layout by replacing that EXE. Install the initial **DevWorkspace Setup.exe** once (or extract the Velopack portable ZIP); subsequent packaged versions can update themselves. The standalone app explains this through **Help → Check for updates**.

## Build packages locally

Use PowerShell from the repository root:

```powershell
./scripts/New-UpdatePackage.ps1 -Version 1.0.0
```

The script uses **Debug**, runs the update and project detection checks, publishes a self-contained Windows x64 application, and packages it with the pinned local `vpk` tool. It prints the new output directory under `.artifacts/updates/`. Neither the script nor packaging publishes anything to GitHub.

Each stable version must be strictly newer than the previous one. For the next update, use `1.0.1`. Keep the app ID **DevWorkspace**, channel **win-x64**, and repository URL consistent.

## Publish through GitHub

After committing and pushing the integration, run **Actions → Package application update → Run workflow** with the new version:

- Leave **publish** unchecked to create a draft release for inspection.
- Enable **publish** to make the version available to installed apps.

The workflow builds from its selected commit and uploads the installer, portable ZIP, full package, and `releases.win-x64.json` feed together using GitHub's workflow token. Publishing a tag or uploading only the EXE is insufficient. Do not overwrite the assets of a published version; publish a newer version instead.

## Verification

```powershell
dotnet run --project tests/AppUpdates/AppUpdates.csproj -c Debug
dotnet run --project tests/ProjectDetection/ProjectDetection.csproj -c Debug
```

Before distributing a new installer, smoke-test a packaged copy and upgrade between two versions. Check both deferred application on launch and **Restart now**, including a second open instance and a network failure. Locally simulated update checks do not establish that a GitHub Release is published.

The startup bootstrap explicitly disables Velopack's automatic apply and guards application of a pending update itself. Do not call `WaitExitThenApplyUpdates` from a background check: its exit timeout can terminate a running app. The call is restricted to the explicit restart path after normal window closure.

References: [Velopack integration](https://docs.velopack.io/integrating/overview), [GitHub hosting](https://docs.velopack.io/distributing/self-hosting).
