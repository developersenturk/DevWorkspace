# Launch improvements — 1.1.0

| Requested improvement | Delivered state |
| --- | --- |
| First-run wizard | Folder picker, project discovery, result summary and overview; can be reopened from Help. |
| Windows code signing | Certificate-store signing support and packaged-app/installer signature and timestamp checks. **Not signed here:** a trusted signing certificate is still required. |
| WinGet installation | Generator verifies the signed public version-specific installer and hash, then emits a manifest for review. **Not registered:** requires a published signed installer, VM validation and acceptance in winget-pkgs. |
| English by default | New installations use English; existing language settings and legacy Turkish sessions are retained. |
| Modern product site | Responsive English/Turkish page, real application screenshot, download links and local documentation pages. Published as an owner-private Sites review version. |
| Shareable profiles | Export/import workspace-relative launch profiles; built-in presets rebind to the receiving workspace, import is a draft and never runs automatically. Linked projects and custom commands are excluded. |
| One-click bug report | Help opens an editable GitHub issue draft with version, Windows version and language, without attaching logs or submitting the issue. |
| Privacy-friendly measurement | Opt-in local counters, visible JSON, manual export and clear controls; no automatic analytics upload. |
| License and contribution guide | MIT, selected by the owner, plus CONTRIBUTING and third-party notices; notices are included in publish output. |
| Exportable workspace report | Standalone printable HTML snapshot; anonymous project numbers by default, optional names, no paths/source/branches/remotes. |

The [website](https://devworkspace-desktop-workbench.developersenturk.chatgpt.site) requires the owner's ChatGPT sign-in. It is not a public product launch. Static source is in `website/dist/`.

Validation: Debug build and self-contained single-file publish; behavioral checks for profile portability, unsafe input rejection, anonymous HTML reports, escaping and opt-out counters; existing project detection, updates, release center, GitHub link, Visual Studio and localization suites. The root executable's main window was observed running version 1.1.0. Site language switching and a 390 px mobile viewport were checked; no horizontal overflow or missing images were observed.

See [distribution prerequisites](distribution.md) for the two external distribution steps and [privacy and sharing](privacy.md) for the exact data behavior.
