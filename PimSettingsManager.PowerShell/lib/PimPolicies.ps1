<#
.SYNOPSIS
	Reads and parses PIM policies (Entra ID roles and PIM groups) from Microsoft Graph
	for the PowerShell edition of PIMSettings Manager. Also provides directory search,
	permission checks, and authentication context lookups. Depends on Graph.ps1.

	Each policy is returned as a PSCustomObject with a nested CurrentSettings object that
	mirrors BulkEditSettings from the desktop app.
#>

Set-StrictMode -Version Latest

function New-PimNotificationSetting {
	param(
		[string[]]$AdditionalRecipients = @(),
		[bool]$DefaultRecipients = $true,
		[bool]$CriticalOnly = $false
	)
	return [pscustomobject]@{
		AdditionalRecipients = @($AdditionalRecipients)
		DefaultRecipients    = $DefaultRecipients
		CriticalOnly         = $CriticalOnly
	}
}

function New-PimCurrentSettings {
	return [pscustomobject]@{
		ActivationMaxDurationHours          = 8
		RequireMfaOnActivation              = $false
		RequireAuthContextOnActivation      = $false
		AuthContextClaimValue               = $null
		RequireJustificationOnActivation    = $false
		RequireTicketOnActivation           = $false
		RequireApprovalToActivate           = $false
		Approvers                           = @()
		AllowPermanentEligibleAssignment    = $true
		ExpireEligibleAfterDays             = 365
		AllowPermanentActiveAssignment      = $true
		ExpireActiveAfterDays               = 180
		RequireMfaOnActiveAssignment        = $false
		RequireJustificationOnActiveAssignment = $false
		EligibleAssignmentAdmin             = (New-PimNotificationSetting)
		EligibleAssignmentAssignee          = (New-PimNotificationSetting)
		EligibleAssignmentApprover          = (New-PimNotificationSetting)
		ActiveAssignmentAdmin               = (New-PimNotificationSetting)
		ActiveAssignmentAssignee            = (New-PimNotificationSetting)
		ActiveAssignmentApprover            = (New-PimNotificationSetting)
		ActivationAdmin                     = (New-PimNotificationSetting)
		ActivationRequestor                 = (New-PimNotificationSetting)
		ActivationApprover                  = (New-PimNotificationSetting)
	}
}

function ConvertFrom-PimDurationDays {
	param([string]$Duration)
	if ([string]::IsNullOrEmpty($Duration)) { return $null }
	# Parse ISO 8601 durations like "P365D".
	if ($Duration.StartsWith('P') -and $Duration.EndsWith('D')) {
		$value = 0
		if ([int]::TryParse($Duration.Substring(1, $Duration.Length - 2), [ref]$value)) {
			return $value
		}
	}
	return $null
}

function Test-PimNodeProperty {
	param($Node, [string]$Name)
	return ($null -ne $Node -and $null -ne $Node.PSObject.Properties[$Name])
}

function Set-PimPolicyRulesFromNode {
	<#
		Parses the @odata rules array on a policy node into the policy's CurrentSettings.
	#>
	param($PolicyNode, $Policy)

	if (-not (Test-PimNodeProperty -Node $PolicyNode -Name 'rules')) { return }
	$settings = $Policy.CurrentSettings

	foreach ($rule in $PolicyNode.rules) {
		if ($null -eq $rule) { continue }
		$ruleType = if (Test-PimNodeProperty -Node $rule -Name '@odata.type') { $rule.'@odata.type' } else { '' }
		$ruleId = if (Test-PimNodeProperty -Node $rule -Name 'id') { $rule.id } else { '' }

		switch ($ruleType) {
			'#microsoft.graph.unifiedRoleManagementPolicyExpirationRule' {
				Set-PimExpirationRule -Rule $rule -RuleId $ruleId -Settings $settings
			}
			'#microsoft.graph.unifiedRoleManagementPolicyEnablementRule' {
				Set-PimEnablementRule -Rule $rule -RuleId $ruleId -Settings $settings
			}
			'#microsoft.graph.unifiedRoleManagementPolicyApprovalRule' {
				Set-PimApprovalRule -Rule $rule -Settings $settings
			}
			'#microsoft.graph.unifiedRoleManagementPolicyNotificationRule' {
				Set-PimNotificationRule -Rule $rule -RuleId $ruleId -Settings $settings
			}
			'#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule' {
				Set-PimAuthContextRule -Rule $rule -Settings $settings
			}
		}
	}
}

function Set-PimExpirationRule {
	param($Rule, [string]$RuleId, $Settings)

	$maxDuration = if (Test-PimNodeProperty -Node $Rule -Name 'maximumDuration') { $Rule.maximumDuration } else { $null }
	$isPermanent = if (Test-PimNodeProperty -Node $Rule -Name 'isExpirationRequired') { [bool]$Rule.isExpirationRequired } else { $false }

	if ($RuleId -match 'Activation' -and $maxDuration) {
		# Parse ISO 8601 hours like "PT8H".
		if ($maxDuration.StartsWith('PT') -and $maxDuration.EndsWith('H')) {
			$hours = 0
			if ([int]::TryParse($maxDuration.Substring(2, $maxDuration.Length - 3), [ref]$hours)) {
				$Settings.ActivationMaxDurationHours = $hours
			}
		}
	}
	elseif ($RuleId -match 'Eligibility') {
		$Settings.AllowPermanentEligibleAssignment = -not $isPermanent
		$days = ConvertFrom-PimDurationDays -Duration $maxDuration
		if ($null -ne $days) { $Settings.ExpireEligibleAfterDays = $days }
	}
	elseif ($RuleId -match 'Assignment') {
		$Settings.AllowPermanentActiveAssignment = -not $isPermanent
		$days = ConvertFrom-PimDurationDays -Duration $maxDuration
		if ($null -ne $days) { $Settings.ExpireActiveAfterDays = $days }
	}
}

function Set-PimEnablementRule {
	param($Rule, [string]$RuleId, $Settings)

	if (-not (Test-PimNodeProperty -Node $Rule -Name 'enabledRules')) { return }
	$rulesList = @($Rule.enabledRules)

	if ($RuleId -match 'Activation') {
		$Settings.RequireMfaOnActivation = $rulesList -contains 'MultiFactorAuthentication'
		$Settings.RequireJustificationOnActivation = $rulesList -contains 'Justification'
		$Settings.RequireTicketOnActivation = $rulesList -contains 'Ticketing'
	}
	elseif ($RuleId -match 'Assignment') {
		$Settings.RequireMfaOnActiveAssignment = $rulesList -contains 'MultiFactorAuthentication'
		$Settings.RequireJustificationOnActiveAssignment = $rulesList -contains 'Justification'
	}
}

function Set-PimApprovalRule {
	param($Rule, $Settings)

	$setting = if (Test-PimNodeProperty -Node $Rule -Name 'setting') { $Rule.setting } else { $null }
	$Settings.RequireApprovalToActivate =
		if (Test-PimNodeProperty -Node $setting -Name 'isApprovalRequired') { [bool]$setting.isApprovalRequired } else { $false }

	$approvers = New-Object System.Collections.Generic.List[object]
	if (Test-PimNodeProperty -Node $setting -Name 'approvalStages') {
		foreach ($stage in $setting.approvalStages) {
			if (-not (Test-PimNodeProperty -Node $stage -Name 'primaryApprovers')) { continue }
			foreach ($approver in $stage.primaryApprovers) {
				if ($null -eq $approver) { continue }

				$odataType = if (Test-PimNodeProperty -Node $approver -Name '@odata.type') { $approver.'@odata.type' } else { '' }
				$id = $null
				if (Test-PimNodeProperty -Node $approver -Name 'userId') { $id = $approver.userId }
				elseif (Test-PimNodeProperty -Node $approver -Name 'groupId') { $id = $approver.groupId }
				elseif (Test-PimNodeProperty -Node $approver -Name 'id') { $id = $approver.id }
				if ([string]::IsNullOrEmpty($id)) { continue }

				$description = if (Test-PimNodeProperty -Node $approver -Name 'description') { $approver.description } else { $null }
				$isGroup = $odataType -match 'group'
				$displayName = if ([string]::IsNullOrWhiteSpace($description)) {
					if ($isGroup) { 'Group' } else { 'User' }
				}
				else { $description }

				$approvers.Add([pscustomobject]@{
					Id          = $id
					DisplayName = $displayName
					IsGroup     = $isGroup
				})
			}
		}
	}
	$Settings.Approvers = $approvers.ToArray()
}

function Set-PimNotificationRule {
	param($Rule, [string]$RuleId, $Settings)

	$recipients = @()
	if (Test-PimNodeProperty -Node $Rule -Name 'notificationRecipients') {
		$recipients = @($Rule.notificationRecipients | Where-Object { $_ })
	}
	$isDefaultEnabled = if (Test-PimNodeProperty -Node $Rule -Name 'isDefaultRecipientsEnabled') { [bool]$Rule.isDefaultRecipientsEnabled } else { $true }
	$criticalOnly = $false
	if (Test-PimNodeProperty -Node $Rule -Name 'notificationLevel') {
		$criticalOnly = ($Rule.notificationLevel -eq 'Critical')
	}

	$notification = New-PimNotificationSetting -AdditionalRecipients $recipients -DefaultRecipients $isDefaultEnabled -CriticalOnly $criticalOnly

	$propertyName = $null
	if ($RuleId -match 'Eligibility') {
		if ($RuleId -match 'Admin') { $propertyName = 'EligibleAssignmentAdmin' }
		elseif ($RuleId -match 'Requestor') { $propertyName = 'EligibleAssignmentAssignee' }
		elseif ($RuleId -match 'Approver') { $propertyName = 'EligibleAssignmentApprover' }
	}
	elseif ($RuleId -match 'Activation') {
		if ($RuleId -match 'Admin') { $propertyName = 'ActivationAdmin' }
		elseif ($RuleId -match 'Requestor') { $propertyName = 'ActivationRequestor' }
		elseif ($RuleId -match 'Approver') { $propertyName = 'ActivationApprover' }
	}
	elseif ($RuleId -match 'Assignment') {
		if ($RuleId -match 'Admin') { $propertyName = 'ActiveAssignmentAdmin' }
		elseif ($RuleId -match 'Requestor') { $propertyName = 'ActiveAssignmentAssignee' }
		elseif ($RuleId -match 'Approver') { $propertyName = 'ActiveAssignmentApprover' }
	}

	if ($propertyName) { $Settings.$propertyName = $notification }
}

function Set-PimAuthContextRule {
	param($Rule, $Settings)

	$Settings.RequireAuthContextOnActivation =
		if (Test-PimNodeProperty -Node $Rule -Name 'isEnabled') { [bool]$Rule.isEnabled } else { $false }
	$Settings.AuthContextClaimValue =
		if (Test-PimNodeProperty -Node $Rule -Name 'claimValue') { $Rule.claimValue } else { $null }
}

function New-PimPolicyObject {
	param(
		[string]$PolicyId,
		[string]$RoleDisplayName,
		[string]$RoleDefinitionId,
		[string]$ScopeId,
		[string]$ScopeDisplayName,
		[string]$Category
	)
	return [pscustomobject]@{
		PolicyId         = $PolicyId
		RoleDisplayName  = $RoleDisplayName
		RoleDefinitionId = $RoleDefinitionId
		ScopeId          = $ScopeId
		ScopeDisplayName = $ScopeDisplayName
		Category         = $Category
		CurrentSettings  = (New-PimCurrentSettings)
	}
}

function Test-PimPermission {
	<#
	.SYNOPSIS
		Verifies the signed-in user can read role management policies.
		Returns $true when access is confirmed.
	#>
	[CmdletBinding()]
	param()
	try {
		$url = "$script:PimGraphBetaBase/policies/roleManagementPolicies?`$filter=scopeId eq '/' and scopeType eq 'DirectoryRole'&`$top=1"
		Invoke-PimGraphRequest -Method GET -Uri $url | Out-Null
		Write-PimLog -Level 'SUCCESS' -Category 'PERMISSION' -Message 'User has required permissions.'
		return $true
	}
	catch {
		Write-PimLog -Level 'WARN' -Category 'PERMISSION' -Message "Permission check failed: $($_.Exception.Message)"
		return $false
	}
}

function Get-PimRoleDefinitionMap {
	$map = @{}
	try {
		$url = "$script:PimGraphBetaBase/roleManagement/directory/roleDefinitions?`$select=id,displayName"
		foreach ($item in (Get-PimGraphPaged -Uri $url)) {
			if ($item.id -and $item.displayName) { $map[$item.id] = $item.displayName }
		}
		Write-PimLog -Level 'INFO' -Category 'API' -Message "Retrieved $($map.Count) role definitions for name resolution."
	}
	catch {
		Write-PimLog -Level 'ERROR' -Category 'API' -Message "Failed to get role definitions: $($_.Exception.Message)"
	}
	return $map
}

function Get-PimPolicyAssignmentMap {
	# Maps policy id -> role definition id.
	$map = @{}
	try {
		$url = "$script:PimGraphBetaBase/policies/roleManagementPolicyAssignments?`$filter=scopeId eq '/' and scopeType eq 'DirectoryRole'&`$select=policyId,roleDefinitionId"
		foreach ($item in (Get-PimGraphPaged -Uri $url)) {
			if ($item.policyId -and $item.roleDefinitionId) { $map[$item.policyId] = $item.roleDefinitionId }
		}
		Write-PimLog -Level 'INFO' -Category 'API' -Message "Retrieved $($map.Count) policy assignments for role mapping."
	}
	catch {
		Write-PimLog -Level 'ERROR' -Category 'API' -Message "Failed to get policy assignments: $($_.Exception.Message)"
	}
	return $map
}

function Get-PimEntraIdRolePolicy {
	<#
	.SYNOPSIS
		Retrieves and parses all Entra ID role policies.
	#>
	[CmdletBinding()]
	param()

	$policies = New-Object System.Collections.Generic.List[object]
	try {
		$roleDefinitions = Get-PimRoleDefinitionMap
		$policyToRoleMap = Get-PimPolicyAssignmentMap

		$url = "$script:PimGraphBetaBase/policies/roleManagementPolicies?`$filter=scopeId eq '/' and scopeType eq 'DirectoryRole'&`$expand=rules"
		foreach ($item in (Get-PimGraphPaged -Uri $url)) {
			$policyId = $item.id
			$roleDefinitionId = if ($policyToRoleMap.ContainsKey($policyId)) { $policyToRoleMap[$policyId] } else { '' }
			$roleName = if ($roleDefinitionId -and $roleDefinitions.ContainsKey($roleDefinitionId)) { $roleDefinitions[$roleDefinitionId] } else { 'Unknown Role' }

			$policy = New-PimPolicyObject -PolicyId $policyId -RoleDisplayName $roleName -RoleDefinitionId $roleDefinitionId `
				-ScopeId '/' -ScopeDisplayName 'Directory' -Category 'EntraIdRoles'
			Set-PimPolicyRulesFromNode -PolicyNode $item -Policy $policy
			$policies.Add($policy)
		}

		Write-PimLog -Level 'SUCCESS' -Category 'API' -Message "Retrieved $($policies.Count) Entra ID role policies."
	}
	catch {
		Write-PimLog -Level 'ERROR' -Category 'API' -Message "Failed to get Entra ID role policies: $($_.Exception.Message)"
		throw
	}
	return $policies
}

function Get-PimGroupResource {
	<#
	.SYNOPSIS
		Discovers PIM-onboarded groups and resolves their display names.
		Returns objects with Id, DisplayName, and Type = 'Group'.
	#>
	[CmdletBinding()]
	param()

	$groups = New-Object System.Collections.Generic.List[object]

	$url = "$script:PimGraphBetaBase/identityGovernance/privilegedAccess/group/resources?`$select=id&`$top=999"
	$values = Get-PimGraphPaged -Uri $url
	if ($values.Count -eq 0) { return $groups }

	$groupIds = New-Object System.Collections.Generic.List[string]
	foreach ($item in $values) {
		$parsed = [Guid]::Empty
		if ($item.id -and [Guid]::TryParse($item.id, [ref]$parsed)) { $groupIds.Add($item.id) }
	}
	Write-PimLog -Level 'INFO' -Category 'API' -Message "Discovered $($groupIds.Count) PIM-onboarded groups."

	for ($i = 0; $i -lt $groupIds.Count; $i += 15) {
		$batch = $groupIds[$i..[Math]::Min($i + 14, $groupIds.Count - 1)]
		$idFilter = ($batch | ForEach-Object { "'$_'" }) -join ','
		$nameUrl = "$script:PimGraphBetaBase/groups?`$filter=id in ($idFilter)&`$select=id,displayName"
		try {
			$result = Invoke-PimGraphRequest -Method GET -Uri $nameUrl
			if (Test-PimNodeProperty -Node $result -Name 'value') {
				foreach ($g in $result.value) {
					$groups.Add([pscustomobject]@{
						Id          = $g.id
						DisplayName = if ($g.displayName) { $g.displayName } else { 'Unknown Group' }
						Type        = 'Group'
					})
				}
			}
		}
		catch {
			Write-PimLog -Level 'ERROR' -Category 'API' -Message "Failed to resolve group display names: $($_.Exception.Message)"
			foreach ($id in $batch) {
				$groups.Add([pscustomobject]@{ Id = $id; DisplayName = $id; Type = 'Group' })
			}
		}
	}
	return $groups
}

function Get-PimGroupPolicy {
	<#
	.SYNOPSIS
		Retrieves and parses PIM policies for the given selected groups.
	#>
	[CmdletBinding()]
	param([Parameter(Mandatory)] $SelectedGroups)

	$policies = New-Object System.Collections.Generic.List[object]

	foreach ($group in $SelectedGroups) {
		$parsed = [Guid]::Empty
		if (-not [Guid]::TryParse($group.Id, [ref]$parsed)) {
			Write-PimLog -Level 'WARN' -Category 'API' -Message "Skipping group with non-GUID ID: '$($group.DisplayName)'."
			continue
		}

		$url = "$script:PimGraphBetaBase/policies/roleManagementPolicyAssignments?`$filter=scopeId eq '$($group.Id)' and scopeType eq 'Group'&`$expand=policy(`$expand=rules)&`$select=policyId,roleDefinitionId,policy"
		try {
			$result = Invoke-PimGraphRequest -Method GET -Uri $url
			if (-not (Test-PimNodeProperty -Node $result -Name 'value')) { continue }

			foreach ($assignment in $result.value) {
				$policyId = if (Test-PimNodeProperty -Node $assignment -Name 'policyId') { $assignment.policyId } else { '' }
				$roleType = if (Test-PimNodeProperty -Node $assignment -Name 'roleDefinitionId') { $assignment.roleDefinitionId } else { 'member' }
				$policyNode = if (Test-PimNodeProperty -Node $assignment -Name 'policy') { $assignment.policy } else { $null }
				if ([string]::IsNullOrEmpty($policyId) -or $null -eq $policyNode) { continue }

				$policy = New-PimPolicyObject -PolicyId $policyId -RoleDisplayName "$($group.DisplayName) ($roleType)" -RoleDefinitionId $roleType `
					-ScopeId $group.Id -ScopeDisplayName 'Group' -Category 'Groups'
				Set-PimPolicyRulesFromNode -PolicyNode $policyNode -Policy $policy
				$policies.Add($policy)
			}
		}
		catch {
			Write-PimLog -Level 'ERROR' -Category 'API' -Message "Failed to get group policies for '$($group.DisplayName)': $($_.Exception.Message)"
		}
	}
	return $policies
}

function Get-PimAuthenticationContext {
	<#
	.SYNOPSIS
		Retrieves the tenant's authentication context class references.
	#>
	[CmdletBinding()]
	param()

	$contexts = New-Object System.Collections.Generic.List[object]
	try {
		$url = "$script:PimGraphBetaBase/identity/conditionalAccess/authenticationContextClassReferences"
		foreach ($item in (Get-PimGraphPaged -Uri $url)) {
			$contexts.Add([pscustomobject]@{
				Id          = $item.id
				DisplayName = if ($item.displayName) { $item.displayName } else { '' }
			})
		}
		Write-PimLog -Level 'SUCCESS' -Category 'API' -Message "Retrieved $($contexts.Count) authentication contexts."
	}
	catch {
		Write-PimLog -Level 'ERROR' -Category 'API' -Message "Failed to get authentication contexts: $($_.Exception.Message)"
	}
	return $contexts
}

function ConvertTo-PimODataLiteral {
	param([string]$Value)
	return $Value.Replace("'", "''")
}

function Search-PimDirectory {
	<#
	.SYNOPSIS
		Searches users and groups by display name / UPN / mail.
		Returns objects with Id, DisplayName, and IsGroup.
	#>
	[CmdletBinding()]
	param([Parameter(Mandatory)][string]$Query)

	$results = New-Object System.Collections.Generic.List[object]
	if ([string]::IsNullOrWhiteSpace($Query) -or $Query.Trim().Length -lt 2) { return $results }

	$encoded = [Uri]::EscapeDataString((ConvertTo-PimODataLiteral -Value $Query.Trim()))

	$userUrl = "$script:PimGraphBetaBase/users?`$filter=startswith(displayName,'$encoded') or startswith(userPrincipalName,'$encoded')&`$top=10&`$select=id,displayName,userPrincipalName"
	$groupUrl = "$script:PimGraphBetaBase/groups?`$filter=startswith(displayName,'$encoded') or startswith(mail,'$encoded')&`$top=10&`$select=id,displayName,mail"

	foreach ($def in @(
		@{ Url = $userUrl; IsGroup = $false },
		@{ Url = $groupUrl; IsGroup = $true }
	)) {
		try {
			$result = Invoke-PimGraphRequest -Method GET -Uri $def.Url
			if (Test-PimNodeProperty -Node $result -Name 'value') {
				foreach ($item in $result.value) {
					$results.Add([pscustomobject]@{
						Id          = $item.id
						DisplayName = if ($item.displayName) { $item.displayName } else { '' }
						IsGroup     = $def.IsGroup
					})
				}
			}
		}
		catch {
			Write-PimLog -Level 'ERROR' -Category 'API' -Message "Directory search failed for '$Query': $($_.Exception.Message)"
		}
	}

	return $results | Sort-Object -Property @{ Expression = 'IsGroup' }, @{ Expression = 'DisplayName' }
}
