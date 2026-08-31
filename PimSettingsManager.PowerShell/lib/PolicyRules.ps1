<#
.SYNOPSIS
	Builds the Microsoft Graph policy PATCH "rules" payload for the PowerShell edition
	of PIMSettings Manager and applies it. Mirrors BuildPolicyRules / UpdatePolicyAsync
	from GraphPimService.cs, preserving the exact rule ids and JSON shapes.

	Desired settings are passed as a hashtable/PSCustomObject with the same fields as
	BulkEditSettings. ApproversRaw is a semicolon-separated list of "user:{id}" /
	"group:{id}" entries; notification rows are objects with AdditionalRecipients,
	DefaultRecipients, and CriticalOnly.
#>

Set-StrictMode -Version Latest

function New-PimRuleTarget {
	param([string]$Caller, [string]$Level)
	return [ordered]@{
		'@odata.type'       = 'microsoft.graph.unifiedRoleManagementPolicyRuleTarget'
		caller              = $Caller
		operations          = @('All')
		level               = $Level
		inheritableSettings = @()
		enforcedSettings    = @()
	}
}

function New-PimRule {
	param([string]$RuleId, [string]$ODataType, $Properties)
	$rule = [ordered]@{}
	foreach ($key in $Properties.Keys) { $rule[$key] = $Properties[$key] }
	$rule['@odata.type'] = $ODataType
	$rule['id'] = $RuleId
	return $rule
}

function Get-PimEnablementRuleValues {
	param($Settings, [bool]$IsActivation)
	$enabled = New-Object System.Collections.Generic.List[string]
	if ($IsActivation) {
		if ($Settings.RequireMfaOnActivation) { $enabled.Add('MultiFactorAuthentication') }
		if ($Settings.RequireJustificationOnActivation) { $enabled.Add('Justification') }
		if ($Settings.RequireTicketOnActivation) { $enabled.Add('Ticketing') }
	}
	else {
		if ($Settings.RequireMfaOnActiveAssignment) { $enabled.Add('MultiFactorAuthentication') }
		if ($Settings.RequireJustificationOnActiveAssignment) { $enabled.Add('Justification') }
	}
	return , $enabled.ToArray()
}

function Get-PimApprovalStages {
	param([string]$ApproversRaw)

	if ([string]::IsNullOrWhiteSpace($ApproversRaw)) { return @() }

	$entries = $ApproversRaw.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() } | Where-Object { $_ }
	$approvers = foreach ($entry in $entries) {
		$type = 'user'
		$id = $entry
		$sep = $entry.IndexOf(':')
		if ($sep -gt 0) {
			$type = $entry.Substring(0, $sep).Trim().ToLowerInvariant()
			$id = $entry.Substring($sep + 1).Trim()
		}

		if ($type -eq 'group') {
			[ordered]@{ '@odata.type' = '#microsoft.graph.groupMembers'; groupId = $id }
		}
		else {
			[ordered]@{ '@odata.type' = '#microsoft.graph.singleUser'; userId = $id }
		}
	}

	return @(
		[ordered]@{
			approvalStageTimeOutInDays        = 1
			isApproverJustificationRequired   = $true
			escalationTimeInMinutes           = 0
			primaryApprovers                  = @($approvers)
			isEscalationEnabled               = $false
			escalationApprovers               = @()
		}
	)
}

function Add-PimNotificationRule {
	param(
		[System.Collections.Generic.List[object]]$Rules,
		[string]$RuleId,
		[string]$RecipientType,
		[string]$Level,
		$Row
	)

	$additionalRecipients = @()
	if ($Row -and $Row.AdditionalRecipients) { $additionalRecipients = @($Row.AdditionalRecipients | Where-Object { $_ }) }

	$caller = if ($RuleId -match 'EndUser') { 'EndUser' } else { 'Admin' }
	$criticalOnly = if ($Row) { [bool]$Row.CriticalOnly } else { $false }
	$defaultRecipients = if ($Row) { [bool]$Row.DefaultRecipients } else { $true }

	$props = [ordered]@{
		notificationType           = 'Email'
		recipientType              = $RecipientType
		notificationLevel          = if ($criticalOnly) { 'Critical' } else { 'All' }
		isDefaultRecipientsEnabled = $defaultRecipients
		notificationRecipients     = @($additionalRecipients)
		target                     = (New-PimRuleTarget -Caller $caller -Level $Level)
	}

	$Rules.Add((New-PimRule -RuleId $RuleId -ODataType '#microsoft.graph.unifiedRoleManagementPolicyNotificationRule' -Properties $props))
}

function Build-PimPolicyRules {
	<#
	.SYNOPSIS
		Builds the full list of policy rules for a PATCH from the desired settings.
	#>
	[CmdletBinding()]
	param([Parameter(Mandatory)] $Settings)

	$rules = New-Object System.Collections.Generic.List[object]

	$activationHours = [int]$Settings.ActivationMaxDurationHours

	# Activation expiration
	$rules.Add((New-PimRule -RuleId 'Expiration_EndUser_Assignment' `
		-ODataType '#microsoft.graph.unifiedRoleManagementPolicyExpirationRule' -Properties ([ordered]@{
			isExpirationRequired = $true
			maximumDuration      = "PT${activationHours}H"
			target               = (New-PimRuleTarget -Caller 'EndUser' -Level 'Assignment')
		})))

	# Activation enablement
	$rules.Add((New-PimRule -RuleId 'Enablement_EndUser_Assignment' `
		-ODataType '#microsoft.graph.unifiedRoleManagementPolicyEnablementRule' -Properties ([ordered]@{
			enabledRules = (Get-PimEnablementRuleValues -Settings $Settings -IsActivation $true)
			target       = (New-PimRuleTarget -Caller 'EndUser' -Level 'Assignment')
		})))

	# Active assignment enablement
	$rules.Add((New-PimRule -RuleId 'Enablement_Admin_Assignment' `
		-ODataType '#microsoft.graph.unifiedRoleManagementPolicyEnablementRule' -Properties ([ordered]@{
			enabledRules = (Get-PimEnablementRuleValues -Settings $Settings -IsActivation $false)
			target       = (New-PimRuleTarget -Caller 'Admin' -Level 'Assignment')
		})))

	# Eligible expiration
	$allowPermanentEligible = [bool]$Settings.AllowPermanentEligibleAssignment
	$eligibleProps = [ordered]@{
		target               = (New-PimRuleTarget -Caller 'Admin' -Level 'Eligibility')
		isExpirationRequired = -not $allowPermanentEligible
	}
	if (-not $allowPermanentEligible) {
		$eligibleProps['maximumDuration'] = "P$([int]$Settings.ExpireEligibleAfterDays)D"
	}
	$rules.Add((New-PimRule -RuleId 'Expiration_Admin_Eligibility' `
		-ODataType '#microsoft.graph.unifiedRoleManagementPolicyExpirationRule' -Properties $eligibleProps))

	# Active expiration
	$allowPermanentActive = [bool]$Settings.AllowPermanentActiveAssignment
	$activeProps = [ordered]@{
		target               = (New-PimRuleTarget -Caller 'Admin' -Level 'Assignment')
		isExpirationRequired = -not $allowPermanentActive
	}
	if (-not $allowPermanentActive) {
		$activeProps['maximumDuration'] = "P$([int]$Settings.ExpireActiveAfterDays)D"
	}
	$rules.Add((New-PimRule -RuleId 'Expiration_Admin_Assignment' `
		-ODataType '#microsoft.graph.unifiedRoleManagementPolicyExpirationRule' -Properties $activeProps))

	# Approval
	$approvalRequired = [bool]$Settings.RequireApprovalToActivate
	$approversRaw = if ($Settings.PSObject.Properties['ApproversRaw']) { $Settings.ApproversRaw } else { $null }
	$stages = if ($approvalRequired -and -not [string]::IsNullOrWhiteSpace($approversRaw)) {
		Get-PimApprovalStages -ApproversRaw $approversRaw
	}
	else { @() }
	$approvalProps = [ordered]@{
		target  = (New-PimRuleTarget -Caller 'EndUser' -Level 'Assignment')
		setting = [ordered]@{
			'@odata.type'                     = 'microsoft.graph.approvalSettings'
			isApprovalRequired                = $approvalRequired
			isApprovalRequiredForExtension    = $false
			isRequestorJustificationRequired  = $true
			approvalMode                      = if ($approvalRequired) { 'SingleStage' } else { 'NoApproval' }
			approvalStages                    = @($stages)
		}
	}
	$rules.Add((New-PimRule -RuleId 'Approval_EndUser_Assignment' `
		-ODataType '#microsoft.graph.unifiedRoleManagementPolicyApprovalRule' -Properties $approvalProps))

	# Authentication context
	$authContextEnabled = [bool]$Settings.RequireAuthContextOnActivation
	$authContextProps = [ordered]@{
		isEnabled  = $authContextEnabled
		claimValue = if ($authContextEnabled) { $Settings.AuthContextClaimValue } else { '' }
		target     = (New-PimRuleTarget -Caller 'EndUser' -Level 'Assignment')
	}
	$rules.Add((New-PimRule -RuleId 'AuthenticationContext_EndUser_Assignment' `
		-ODataType '#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule' -Properties $authContextProps))

	# Notifications
	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Admin_Admin_Eligibility'      -RecipientType 'Admin'     -Level 'Eligibility' -Row $Settings.EligibleAssignmentAdmin
	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Requestor_Admin_Eligibility'  -RecipientType 'Requestor' -Level 'Eligibility' -Row $Settings.EligibleAssignmentAssignee
	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Approver_Admin_Eligibility'   -RecipientType 'Approver'  -Level 'Eligibility' -Row $Settings.EligibleAssignmentApprover

	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Admin_Admin_Assignment'       -RecipientType 'Admin'     -Level 'Assignment'  -Row $Settings.ActiveAssignmentAdmin
	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Requestor_Admin_Assignment'   -RecipientType 'Requestor' -Level 'Assignment'  -Row $Settings.ActiveAssignmentAssignee
	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Approver_Admin_Assignment'    -RecipientType 'Approver'  -Level 'Assignment'  -Row $Settings.ActiveAssignmentApprover

	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Admin_EndUser_Assignment'     -RecipientType 'Admin'     -Level 'Assignment'  -Row $Settings.ActivationAdmin
	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Requestor_EndUser_Assignment' -RecipientType 'Requestor' -Level 'Assignment'  -Row $Settings.ActivationRequestor
	Add-PimNotificationRule -Rules $rules -RuleId 'Notification_Approver_EndUser_Assignment'  -RecipientType 'Approver'  -Level 'Assignment'  -Row $Settings.ActivationApprover

	return $rules
}

function Update-PimPolicy {
	<#
	.SYNOPSIS
		Applies the desired settings to a single policy via a single PATCH request.
		Returns $true on success.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] $Policy,
		[Parameter(Mandatory)] $Settings
	)

	try {
		$rules = Build-PimPolicyRules -Settings $Settings
		$body = @{ rules = @($rules) }

		$url = "$script:PimGraphBetaBase/policies/roleManagementPolicies/$($Policy.PolicyId)"
		Invoke-PimGraphRequest -Method PATCH -Uri $url -Body $body | Out-Null

		Write-PimLog -Level 'SUCCESS' -Category 'SETTINGS' -Message "Updated $($rules.Count) rules for '$($Policy.RoleDisplayName)' in a single request."
		return $true
	}
	catch {
		Write-PimLog -Level 'ERROR' -Category 'SETTINGS' -Message "Failed to update policy for '$($Policy.RoleDisplayName)': $($_.Exception.Message)"
		return $false
	}
}
