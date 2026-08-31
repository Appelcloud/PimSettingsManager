# PIMSettings Manager — PowerShell Edition

A **zero-dependency** PowerShell edition of PIMSettings Manager. It manages Microsoft
Entra Privileged Identity Management (PIM) **role policies** and **PIM group policies**
by calling the Microsoft Graph REST API directly.

There is **nothing to install**: no modules, no MSAL, no build step. If a machine has
PowerShell, it can run this script.

## Features (parity with the desktop app)

- Sign in interactively through your browser (no modules required).
- Manage **Entra ID role** policies and **PIM group** policies.
- Configure the full set of policy settings:
  - Activation maximum duration.
  - On-activation requirements: MFA, authentication context, justification, ticket.
  - Require approval to activate, with **user and group** approvers.
  - Eligible/active assignment expiration and permanent-assignment rules.
  - MFA/justification on active assignment.
  - Notification rules (admin / assignee / approver, for eligibility, assignment and activation).
- Preview all pending changes before applying.
- Apply changes with a per-target success/failure summary.
- Readable session log written to disk.

## Prerequisites

- **PowerShell 7+** (recommended) or Windows PowerShell 5.1.
- A user account with permission to manage PIM policies (for example, Privileged Role
  Administrator). You will be prompted to consent to the required Graph permissions on
  first sign-in.
- Outbound access to `https://graph.microsoft.com` and `https://login.microsoftonline.com`.
- A default web browser (used once for interactive sign-in).

## How to run

```powershell
# From this folder
pwsh -File .\Invoke-PimSettingsManager.ps1
```

or, from an existing PowerShell 7 session:

```powershell
.\Invoke-PimSettingsManager.ps1
```

On first launch your browser opens for sign-in. After you sign in, the script guides you
through selecting a category, choosing roles or groups, configuring settings, reviewing a
preview, and confirming before any changes are written.

## Authentication

The script uses the interactive **authorization code flow with PKCE** against the public
Microsoft Graph PowerShell client. It briefly starts a local `http://localhost` listener
to capture the sign-in redirect, then exchanges the code for an access token. No secrets
are stored and no modules are required.

## Logs

A session log is written to:

```
%LOCALAPPDATA%\PIMSettingsManager\Logs\PIMSettingsManager_<timestamp>.log
```

Logs contain informational messages, warnings, errors, and a record of the configuration
changes made — without raw API query noise.

## Notes

- The **Azure Resources** category is intentionally not included (it is under construction
  in the desktop app as well).
- This edition lives alongside the desktop app and does not modify it.
