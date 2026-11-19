## Daily workspace tools

- Refreshed the startup screen with a branded layout, version information and real preparation stages in light and dark themes. Startup is not artificially delayed.

- Operation Center, batch fetch, worktree management, project inspection, tracked findings and workspace tool tables now use DevExpress GridControl consistently, including dark/light themes, row selection and operation actions.

- Saved layout controls are no longer rebuilt while the Windows menu opens. They are prepared at startup and refreshed after saving a layout, avoiding popup focus/capture disruption during opening.

- Launch profiles now support terminals, saved commands and Compose steps.
- Added project handoff notes, a Git overview, and managed command PID/port/log tracking with restart controls.
- Environment checks inspect Node version requirements, missing .env keys and Compose container states.
- Added cross-project content search with Ctrl+Shift+F, .NET/JavaScript/Compose dependency impact views, and built-in/custom project templates with file previews.

## Get started faster

- A first-run guide helps you choose a development folder, discover projects and open the workspace overview. Reopen it from Help → Getting started.
- New installations start in English. Existing language preferences are preserved; change your language in Settings.

## Share your workflow

- Export and import launch profiles from the profile editor. Built-in commands are rebound to the current workspace; imports open as drafts for review and never run automatically.
- Export a standalone HTML workspace report from File. Names are anonymous by default; you can include project names. Paths, source code, branches and remote URLs are excluded.

## Feedback under your control

- Help → Report a bug opens an editable GitHub issue draft with app version, Windows version and language. No logs or source files are attached; nothing is submitted automatically.
- Optional local feature counters are disabled by default. View, export or clear them from Help. Counters stay on your computer and are never uploaded automatically.

## Project and distribution

- Original DevWorkspace code is now MIT licensed, with a contribution guide and third-party notices.
- Signing verification and a WinGet manifest generator prepare future signed distribution. A trusted certificate, published installer and WinGet acceptance are still required.
