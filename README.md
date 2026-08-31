# PIMSettings Manager

<p align="center">
  <img src="docs/lockup-horizontal.png" alt="PIMSettings Manager" height="80">
</p>

A Windows desktop tool for configuring PIM (Privileged Identity Management) role settings across multiple Entra ID roles and PIM-enabled groups in bulk. Save hours of manual work by applying activation, assignment, and notification settings to many roles at once.

## Features

- **Bulk edit Entra ID role settings** — Select multiple directory roles and configure them simultaneously
- **Bulk edit PIM for Groups settings** — Apply settings to PIM-enabled security groups
- **Per-category configuration** — Set different policies for Entra ID roles vs. PIM Groups in a single session
- **Preview all changes before applying** — Review a detailed diff grouped by category before committing
- **Configure activation, assignment & notification policies** — Full control over MFA, justification, approval, expiration, authentication contexts, and email notifications
- **Detailed logging** — Full audit trail logged locally for troubleshooting and compliance

## Screenshots

### Home
![Home](docs/screenshots/screenshot-home.png)

### Role Selection
![Role Selection](docs/screenshots/screenshot-roles.png)

### Settings Configuration
![Settings](docs/screenshots/screenshot-settings.png)

## Requirements

- Windows 10 (build 17763+) or Windows 11
- .NET 8 Desktop Runtime
- Entra ID account with `RoleManagement.ReadWrite.Directory` permissions
- Microsoft Entra ID P2 license (for PIM)

## Getting Started

The repository ships a ready-to-run, self-contained build of the app in the root
[`tool/`](tool/) folder — no installation and no .NET runtime required.

1. Clone or download this repository
2. Run `tool\PIMSettings Manager.exe`
3. Sign in with your Entra ID credentials
4. Select the PIM categories you want to manage (Entra ID Roles, PIM for Groups, or both)
5. Select roles, configure settings per category, preview all changes, and apply!

## Repository Layout

| Path | Description |
|---|---|
| `tool/` | Committed self-contained (win-x64) build of the GUI — run `tool\PIMSettings Manager.exe` |
| `src/` | Application source: solution and the `BulkPimRoleSettings` WinUI 3 project |
| `docs/` | Documentation site assets and screenshots |
| `publish-tool.ps1` | Rebuilds the `tool/` folder from source |

## Build from Source

```powershell
# Open the solution
start src\BulkPimRoleSettings.slnx

# ...or rebuild the committed root tool from the command line
pwsh -File .\publish-tool.ps1
```

The published output is written to `tool/`. Use `-Runtime win-arm64` or `-Runtime win-x86`
with `publish-tool.ps1` to target other architectures.

## Permissions Required

The application uses delegated permissions via MSAL (Microsoft Authentication Library):

| Permission | Purpose |
|---|---|
| `RoleManagement.ReadWrite.Directory` | Read and update PIM role policies |
| `Directory.Read.All` | List roles, groups, and users |

## Built With

- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) — Modern Windows UI framework
- [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/) — Windows application platform
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) — MVVM architecture
- [Microsoft Identity Client (MSAL)](https://learn.microsoft.com/en-us/entra/msal/) — Authentication
- [Microsoft Graph API](https://learn.microsoft.com/en-us/graph/) — PIM policy management

## Author

**Alexander Appelby** — Microsoft 365 & Security MVP

- [Blog](https://blog.appelcloud.dk)
- [Tools Hub](https://tools.appelcloud.dk)
- [MVP Profile](https://mvp.microsoft.com/en-US/mvp/profile/12ef06cd-cdab-4545-b87d-b7b682dd0d67)
- [LinkedIn](https://dk.linkedin.com/in/alexander-appelby)

## License

This project is proprietary. All rights reserved.
