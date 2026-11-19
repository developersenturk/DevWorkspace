# Privacy and sharing

DevWorkspace does not upload source code as part of project discovery. Git, package security checks, GitHub features and update checks contact their respective services when used; this is separate from usage measurement.

**Local usage counters are disabled by default.** Help → Local usage counters lets you opt in, see all collected counts, export a JSON file, turn counting off or clear the counts. Only fixed feature names and integer counts are stored in the local session file. There are no event timestamps, user/device IDs, project names, paths, commands or automatic telemetry uploads. Turning counting off preserves old counts until you clear them. Counts are best effort, saved with your session; they are not cross-process analytics. To contribute usage feedback, export the counters, review the JSON and choose how to share it yourself.

**Report a bug** opens an editable GitHub issue draft in your browser with the application version, Windows version and interface language. It does not submit an issue, collect logs or attach files. GitHub receives those fields when the draft URL is opened; inspect the draft before submitting it.

**Workspace reports** are standalone HTML files with project type, last scanned Git status and changed-file counts. Names are anonymous by default; choose Yes only if you want project names included. Files do not include local paths, branches, remotes or source code. A report is a snapshot, not a fresh scan. Review before sharing. Existing ZIP workspace exports contain configuration and project metadata and have different privacy properties.

**Shared launch profiles** contain workspace-relative project identifiers and signatures of built-in command presets. They exclude linked projects and custom commands. Import matches the current workspace's available presets and opens a draft for review. It never runs or saves a profile automatically. Built-in commands can execute repository scripts, so only run profiles against repositories you trust.
