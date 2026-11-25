# Work and maintenance

Open **File → Work and maintenance**, or use its button in the Development hub.

## Work sets

Select related projects (Ctrl/Shift supports multiple selection), enter a work name and notes, optionally choose a launch profile, then **Save work set**. **Select profile projects** selects the projects used by that profile. Each project captures its current branch or detached commit, worktree repository path and handoff note. Work sets are stored in the local workspace session.

**Resume this work** stops current workspace profile processes, validates every repository and restores branches and handoff notes before opening the projects. Check **Also start services** to run the saved profile after restoring. A dirty repository is allowed when its branch already matches; changing a dirty repository's branch is blocked. Missing projects/branches or branches in another worktree block restoration. No force checkout or automatic stash is used. If a switch fails after another succeeds, the app attempts to return clean repositories to their prior branches and reports any repositories that require inspection.

## Error solutions

Choose a previous managed operation or paste an error, select its project and click **Find solutions in log**. Local rules recognize common port conflicts, .NET SDK/assets errors, JavaScript/Python dependency errors, missing environment variables and refused service connections. Suggestions describe possible causes; they do not establish a diagnosis. Unknown errors remain available in the complete log.

Select a suggestion to see its exact command. **Run selected command** executes it through the operation center. Restore/install commands may download packages or execute package-manager lifecycle scripts. Tool setup and environment checks remain separate actions. Port inspection lists current process names/PIDs and never terminates a process automatically.

## Change impact

Select a project, optionally start from its locally changed files, then analyze impact. The app follows reverse .NET `ProjectReference` and JavaScript `file:`, `link:` and `workspace:` references. Transitive consumers and test manifests are included; dependency cycles are deduplicated. Shared MSBuild configuration changes conservatively include manifests below that configuration folder. Without local changes, analysis covers the selected project.

The displayed test commands can be run sequentially. .NET test commands explicitly use `-c Debug`; JavaScript commands follow detected npm/pnpm/yarn lockfiles. A failed command stops the sequence. Runtime HTTP dependencies, external package consumers and unresolved references are outside this static analysis. Review diagnostics and rerun the analysis after changing dependencies.

## Environment variants

Choose a base profile and a project, give the new profile and environment a name, then optionally select an environment file and override a readiness port. **Selected project only** creates a smaller profile such as a frontend-only setup. A Docker variant can be derived from a profile that includes a Compose startup step. Existing commands remain editable in the profile editor.

Environment file paths must be relative and inside the corresponding project folder. Files are read when commands start, and values are injected into that command's process environment; the application does not replace `.env` or change global Windows variables. Comments, `export KEY=value` and single-line quoted values are supported. Shell expansion/interpolation is not performed. The `DEVWORKSPACE_` prefix is reserved. Files are limited to 1 MB. Retrying/restarting a managed command rereads its selected environment file.

Shared profile/team JSON preserves `environmentName` and `environmentFiles` (project-path → relative file-path). File contents remain local. Keep real environment files outside version control as appropriate; share an example with key names separately.

## Resources and cleanup

The resources tab samples app-managed commands and child processes every two seconds. RAM is the sum of working sets; CPU is normalized to all logical processors' total capacity and appears after the first sample. PID start times prevent attributing reused PIDs to old operations. Independently opened IDE/terminal processes are outside this view.

Scan outputs and caches to preview known .NET `bin`/`obj`, JavaScript `node_modules`/`.next`/`.nuxt`/`dist`, Dart `build`/`.dart_tool`, Rust `target`, Java build outputs and Python caches. Ecosystem-specific project files are required for output names that could otherwise be source directories. `.git`, reparse points and junctions are excluded. Incomplete/inaccessible scans cannot be cleaned.

Select folders to inspect file names and sizes. Preview displays the first 300 files per folder; the confirmed operation covers **all contents** of selected folders. Git-tracked files and active managed projects block cleanup. Metadata fingerprints, Git tracking and running-command checks are repeated immediately before cleanup. Managed command startup is blocked for a project while its cleanup is active.

Choose **Move to recycle bin** or **Delete permanently**, then confirm the exact target list. Targets are moved to a temporary sibling folder within the project and revalidated before removal. A failed or cancelled validation preserves/restores the staged files and reports their location. Recycling does not reclaim physical disk space until the bin is emptied. Only permanent deletion immediately removes those files from the filesystem. Reinstall packages or rebuild to recreate generated outputs.
