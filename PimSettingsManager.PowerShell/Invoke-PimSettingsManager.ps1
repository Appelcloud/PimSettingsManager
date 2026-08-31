<#
.SYNOPSIS
	PIMSettings Manager - zero-dependency PowerShell edition.

.DESCRIPTION
	Interactive console tool to bulk-configure Microsoft Entra PIM policies for
	Entra ID roles and PIM groups by calling the Microsoft Graph REST API directly.
	No modules to install: it uses browser-based sign-in (auth code flow with PKCE)
	and raw REST calls. Runs anywhere PowerShell runs.

.EXAMPLE
	pwsh -File .\Invoke-PimSettingsManager.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Load library modules (order matters: ConsoleUi provides Write-PimLog used by others).
$libDir = Join-Path $PSScriptRoot 'lib'
foreach ($module in @('ConsoleUi.ps1', 'Auth.ps1', 'Graph.ps1', 'PimPolicies.ps1', 'PolicyRules.ps1')) {
	. (Join-Path $libDir $module)
}

function Read-PimNotificationRow {
	param([string]$Label)

	Write-Host ''
	Write-Host "Notifications - $Label" -ForegroundColor Cyan
	$default = Read-PimYesNo -Question '  Send to default recipients?' -Default $true
	$critical = Read-PimYesNo -Question '  Critical emails only?' -Default $false
	$recipientsRaw = Read-PimText -Prompt '  Additional recipients (semicolon-separated emails, blank for none)'
	$recipients = if ([string]::IsNullOrWhiteSpace($recipientsRaw)) {
		@()
	}
	else {
		@($recipientsRaw.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
	}
	return New-PimNotificationSetting -AdditionalRecipients $recipients -DefaultRecipients $default -CriticalOnly $critical
}

function Read-PimApprovers {
	<#
		Interactively searches the directory and returns a semicolon-separated
		"user:{id}"/"group:{id}" approver string.
	#>
	$entries = New-Object System.Collections.Generic.List[string]

	Write-Host ''
	Write-Host 'Add approvers (search by name; blank query when done).' -ForegroundColor Cyan
	while ($true) {
		$query = Read-PimText -Prompt '  Search approver'
		if ([string]::IsNullOrWhiteSpace($query)) { break }

		$results = @(Search-PimDirectory -Query $query)
		if ($results.Count -eq 0) {
			Write-Host '  No matches.' -ForegroundColor Yellow
			continue
		}

		$labels = $results | ForEach-Object {
			$kind = if ($_.IsGroup) { 'Group' } else { 'User' }
			"$($_.DisplayName) [$kind]"
		}
		$index = Read-PimMenuChoice -Title 'Select approver' -Options @($labels + 'Cancel')
		if ($index -eq $results.Count) { continue }

		$chosen = $results[$index]
		$prefix = if ($chosen.IsGroup) { 'group' } else { 'user' }
		$entries.Add("$prefix`:$($chosen.Id)")
		Write-Host "  Added: $($chosen.DisplayName)" -ForegroundColor Green
	}

	return ($entries -join ';')
}

function Read-PimDesiredSettings {
	<#
		Captures the full desired settings interactively and returns a settings object
		compatible with Build-PimPolicyRules.
	#>
	$settings = New-PimCurrentSettings
	Add-Member -InputObject $settings -NotePropertyName 'ApproversRaw' -NotePropertyValue '' -Force

	Write-PimHeader -Title 'Configure settings'

	# Activation
	$settings.ActivationMaxDurationHours = [int](Read-PimNumber -Prompt 'Activation max duration (hours)' -Default 8)
	$settings.RequireMfaOnActivation = Read-PimYesNo -Question 'Require MFA on activation?' -Default $true
	$settings.RequireJustificationOnActivation = Read-PimYesNo -Question 'Require justification on activation?' -Default $true
	$settings.RequireTicketOnActivation = Read-PimYesNo -Question 'Require ticket on activation?' -Default $false

	$settings.RequireAuthContextOnActivation = Read-PimYesNo -Question 'Require authentication context on activation?' -Default $false
	if ($settings.RequireAuthContextOnActivation) {
		$contexts = @(Get-PimAuthenticationContext)
		if ($contexts.Count -gt 0) {
			$labels = $contexts | ForEach-Object { "$($_.DisplayName) ($($_.Id))" }
			$index = Read-PimMenuChoice -Title 'Select authentication context' -Options $labels
			$settings.AuthContextClaimValue = $contexts[$index].Id
		}
		else {
			$settings.AuthContextClaimValue = Read-PimText -Prompt 'Auth context claim value (e.g. c1)'
		}
	}

	# Approval
	$settings.RequireApprovalToActivate = Read-PimYesNo -Question 'Require approval to activate?' -Default $false
	if ($settings.RequireApprovalToActivate) {
		$settings.ApproversRaw = Read-PimApprovers
	}

	# Assignment
	$settings.AllowPermanentEligibleAssignment = Read-PimYesNo -Question 'Allow permanent eligible assignment?' -Default $false
	if (-not $settings.AllowPermanentEligibleAssignment) {
		$settings.ExpireEligibleAfterDays = [int](Read-PimNumber -Prompt 'Expire eligible after (days)' -Default 365)
	}
	$settings.AllowPermanentActiveAssignment = Read-PimYesNo -Question 'Allow permanent active assignment?' -Default $false
	if (-not $settings.AllowPermanentActiveAssignment) {
		$settings.ExpireActiveAfterDays = [int](Read-PimNumber -Prompt 'Expire active after (days)' -Default 180)
	}
	$settings.RequireMfaOnActiveAssignment = Read-PimYesNo -Question 'Require MFA on active assignment?' -Default $false
	$settings.RequireJustificationOnActiveAssignment = Read-PimYesNo -Question 'Require justification on active assignment?' -Default $false

	# Notifications (optional)
	if (Read-PimYesNo -Question 'Configure notification rules?' -Default $false) {
		$settings.EligibleAssignmentAdmin    = Read-PimNotificationRow -Label 'Eligible assignment - Admin'
		$settings.EligibleAssignmentAssignee = Read-PimNotificationRow -Label 'Eligible assignment - Assignee'
		$settings.EligibleAssignmentApprover = Read-PimNotificationRow -Label 'Eligible assignment - Approver'
		$settings.ActiveAssignmentAdmin      = Read-PimNotificationRow -Label 'Active assignment - Admin'
		$settings.ActiveAssignmentAssignee   = Read-PimNotificationRow -Label 'Active assignment - Assignee'
		$settings.ActiveAssignmentApprover   = Read-PimNotificationRow -Label 'Active assignment - Approver'
		$settings.ActivationAdmin            = Read-PimNotificationRow -Label 'Activation - Admin'
		$settings.ActivationRequestor        = Read-PimNotificationRow -Label 'Activation - Requestor'
		$settings.ActivationApprover         = Read-PimNotificationRow -Label 'Activation - Approver'
	}

	return $settings
}

function Get-PimSelectedTargets {
	<#
		Prompts for the category and returns the selected policy targets to update.
	#>
	$categoryIndex = Read-PimMenuChoice -Title 'Choose a category' -Options @(
		'Entra ID roles',
		'PIM groups'
	)

	if ($categoryIndex -eq 0) {
		Write-Host 'Loading Entra ID role policies...' -ForegroundColor Gray
		$policies = @(Get-PimEntraIdRolePolicy)
		return Read-PimMultiSelect -Title 'Select roles to update' -Items $policies -DisplayProperty 'RoleDisplayName'
	}

	Write-Host 'Discovering PIM groups...' -ForegroundColor Gray
	$groups = @(Get-PimGroupResource)
	if ($groups.Count -eq 0) {
		Write-Host 'No PIM-onboarded groups found.' -ForegroundColor Yellow
		return @()
	}
	$selectedGroups = Read-PimMultiSelect -Title 'Select groups' -Items $groups -DisplayProperty 'DisplayName'
	if (@($selectedGroups).Count -eq 0) { return @() }

	Write-Host 'Loading group policies...' -ForegroundColor Gray
	$policies = @(Get-PimGroupPolicy -SelectedGroups $selectedGroups)
	return Read-PimMultiSelect -Title 'Select group policies to update' -Items $policies -DisplayProperty 'RoleDisplayName'
}

function Invoke-PimApply {
	param([Parameter(Mandatory)] $Targets, [Parameter(Mandatory)] $Settings)

	$results = New-Object System.Collections.Generic.List[object]
	foreach ($target in $Targets) {
		Write-Host "Applying to '$($target.RoleDisplayName)'..." -ForegroundColor Gray
		$success = Update-PimPolicy -Policy $target -Settings $Settings
		$results.Add([pscustomobject]@{ Target = $target.RoleDisplayName; Success = $success })
	}
	return $results
}

function Invoke-Main {
	$logPath = Initialize-PimLog
	Write-PimHeader -Title 'PIMSettings Manager (PowerShell)'
	Write-Host "Log file: $logPath" -ForegroundColor DarkGray

	try {
		Write-Host 'Opening browser for sign-in...' -ForegroundColor Gray
		Connect-PimGraph | Out-Null
		$account = Get-PimSignedInAccount
		if ($account -and $account.Username) {
			Write-PimLog -Level 'SUCCESS' -Category 'AUTH' -Message "Signed in as $($account.Username)."
		}
		else {
			Write-PimLog -Level 'SUCCESS' -Category 'AUTH' -Message 'Signed in.'
		}
	}
	catch {
		Write-PimLog -Level 'ERROR' -Category 'AUTH' -Message "Sign-in failed: $($_.Exception.Message)"
		return
	}

	if (-not (Test-PimPermission)) {
		Write-Host 'You do not have the required permissions to manage PIM policies.' -ForegroundColor Red
		return
	}

	while ($true) {
		try {
			$targets = @(Get-PimSelectedTargets)
		}
		catch {
			Write-PimLog -Level 'ERROR' -Category 'API' -Message "Failed to load policies: $($_.Exception.Message)"
			$targets = @()
		}

		if ($targets.Count -eq 0) {
			Write-Host 'No targets selected.' -ForegroundColor Yellow
		}
		else {
			$settings = Read-PimDesiredSettings
			Show-PimSettingsPreview -Settings $settings -Targets $targets

			if (Read-PimYesNo -Question 'Apply these changes?' -Default $false) {
				$results = Invoke-PimApply -Targets $targets -Settings $settings
				Show-PimApplyResult -Results $results
			}
			else {
				Write-PimLog -Level 'INFO' -Category 'SETTINGS' -Message 'User cancelled before applying changes.'
				Write-Host 'No changes applied.' -ForegroundColor Yellow
			}
		}

		if (-not (Read-PimYesNo -Question 'Perform another operation?' -Default $false)) { break }
	}

	Disconnect-PimGraph
	Write-Host ''
	Write-Host "Done. Full log: $logPath" -ForegroundColor Cyan
}

Invoke-Main
