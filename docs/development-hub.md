# Development hub

For work sets, error solutions, change impact, environment variants and resource cleanup, see [Work and maintenance](work-and-maintenance.md).

Open **File → Development hub** after opening a workspace.

1. Use **Edit profiles** to add projects and commands in startup order.
2. For a long-running service, set its **Readiness port** to a TCP port on `127.0.0.1`. For a build or setup task, choose **Wait for task completion** instead. Use **Apply to selected step** when editing an existing step. The default timeout is 60 seconds; a step without either readiness option starts the next step immediately.
3. Use **Check environment** to review the selected profile's requirements. Missing tools, SDK failures and missing `.env` keys prevent startup. **Tool setup** opens the existing installer interface. **Create .env from example** copies `.env.example`, `.env.sample` or `.env.template` without overwriting an existing `.env`; edit local values yourself.
4. Choose **Start development**. Failed tasks, occupied readiness ports and readiness timeouts stop the startup sequence. Managed processes started by a failed run are cancelled. **Stop development** cancels pending startup and managed processes for profiles in this workspace. Editor and terminal windows opened by a profile are independent windows.
5. Use **Running applications** for project filters, log text search, port/PID inspection, restart and stopping commands. Select a compiler/stack location or place the caret on its line, then use **Open file from log**. VS Code opens at the location when available; Notepad is the fallback.

The **Where did I leave off?** tab shows the selected project's handoff note, current branch and changed files. The last successfully started profile is saved with the workspace. **Working repositories** compares distinct Git repositories in the selected profile and can open all of its projects. Ahead/behind values refer to the last fetched upstream state; use Git → Fetch to contact remotes.

The built-in **Docker Compose start** profile command (`docker.exe compose up`) also tracks the containers already running before startup. Stopping the profile stops containers started by that command and preserves containers that were already running. Custom Docker commands retain their own lifecycle semantics; use the Compose tools for those services.

Workspace files and Git metadata are watched in the background, including while the hub is open. The main status bar shows the last successful scan time. Environment results show their own check time and age; run checks again after changing settings.

## Share a development setup

In **Team configuration**, choose **Preview from profiles**. Review or edit the JSON and save it as `devworkspace.development.json` in the workspace root. Commit this file with the repository if you want to share it. On another machine, open the same workspace, choose **Read repository file**, review the commands and choose **Import preview**. Matching profile names are updated; other local profiles are retained. Import does not execute commands.

Example:

```json
{
  "schemaVersion": 1,
  "profiles": [
    {
      "name": "API and client",
      "steps": [
        {
          "project": "api",
          "command": {
            "name": "API",
            "executable": "dotnet.exe",
            "arguments": "run --project \"${workspace}\\api\\Api.csproj\""
          },
          "readyPort": 5100,
          "timeoutSeconds": 90
        },
        {
          "project": "client",
          "command": {
            "name": "Client",
            "executable": "cmd.exe",
            "arguments": "/d /c npm.cmd run dev"
          },
          "readyPort": 5173,
          "timeoutSeconds": 60
        }
      ]
    }
  ],
  "requiredTools": ["dotnet.exe", "node.exe"]
}
```

Use the project paths shown in the profile editor; they must match projects discovered in the destination workspace. Shared project paths are relative. Executables use tool names; `${workspace}` in command arguments resolves to the destination root. `action` defaults to `default`, and `waitForExit` defaults to `false`. `terminal` steps cannot have commands or readiness rules. Tool requirements contain executable names and are checked on PATH or through known tool locations.

Keep credentials and machine-specific values in local environment files, outside this configuration. The editor supports custom commands, so inspect the exact JSON before sharing or importing it. The existing preset-only profile import/export remains available for sharing a single profile and now preserves readiness rules too.
