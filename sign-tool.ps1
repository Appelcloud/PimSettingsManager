<#
.SYNOPSIS
	Code-signs the committed GUI executable (tool/PIMSettings Manager.exe) using
	Azure Artifact Signing (formerly Trusted Signing).

.DESCRIPTION
	Runs locally on Windows x64. Authenticates with your Azure login (az login /
	AzureCliCredential), locates SignTool and the Azure.CodeSigning dlib plugin,
	writes a temporary metadata.json from the supplied account details, signs the
	executable with a trusted timestamp, and verifies the resulting signature.

	Prerequisites (install once):
	  winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
	  winget install -e --id Microsoft.WindowsSDK           # provides signtool.exe
	  Azure CLI (az) + 'az login'
	  The signing identity must hold the 'Trusted Signing Certificate Profile Signer'
	  role on the certificate profile, and identity validation must be Completed.

.PARAMETER Endpoint
	Region-specific Artifact Signing account URI, e.g. https://weu.codesigning.azure.net/
	(shown in the Azure portal as the account 'Account URI').

.PARAMETER AccountName
	The Artifact Signing (Trusted Signing) account NAME — not your user/email/app id.

.PARAMETER CertificateProfileName
	The certificate profile name under the signing account.

.PARAMETER FilePath
	File to sign. Defaults to the committed tool exe.

.EXAMPLE
	az login
	pwsh -File .\sign-tool.ps1 -Endpoint https://weu.codesigning.azure.net/ `
		-AccountName MySigningAccount -CertificateProfileName MyProfile
#>

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[ValidatePattern('^https://.*\.codesigning\.azure\.net/?$')]
	[string]$Endpoint,

	[Parameter(Mandatory)]
	[string]$AccountName,

	[Parameter(Mandatory)]
	[string]$CertificateProfileName,

	[string]$FilePath = (Join-Path $PSScriptRoot 'tool\PIMSettings Manager.exe'),

	[string]$TimestampUrl = 'http://timestamp.acs.microsoft.com'
)

$ErrorActionPreference = 'Stop'

function Find-FirstFile {
	param([string[]]$Candidates, [string]$SearchRoot, [string]$LeafName)

	foreach ($c in $Candidates) {
		if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path }
	}
	if ($SearchRoot -and (Test-Path $SearchRoot)) {
		$found = Get-ChildItem -Path $SearchRoot -Filter $LeafName -Recurse -File -ErrorAction SilentlyContinue |
			Sort-Object FullName -Descending | Select-Object -First 1
		if ($found) { return $found.FullName }
	}
	return $null
}

function Resolve-SignTool {
	$cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
	$candidates = @(if ($cmd) { $cmd.Source })
	$path = Find-FirstFile -Candidates $candidates -SearchRoot 'C:\Program Files (x86)\Windows Kits\10\bin' -LeafName 'signtool.exe'
	if (-not $path) {
		throw "signtool.exe not found. Install the Windows SDK (winget install -e --id Microsoft.WindowsSDK) or add signtool to PATH."
	}
	# Prefer an x64 signtool when the search returned an arbitrary arch.
	if ($path -notmatch '\\x64\\') {
		$x64 = Find-FirstFile -Candidates @() -SearchRoot 'C:\Program Files (x86)\Windows Kits\10\bin' -LeafName 'signtool.exe'
		if ($x64 -and $x64 -match '\\x64\\') { $path = $x64 }
	}
	return $path
}

function Resolve-Dlib {
	$candidates = @(
		'C:\Program Files\Microsoft\ArtifactSigningClientTools\bin\x64\Azure.CodeSigning.Dlib.dll',
		'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
		'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\x64\Azure.CodeSigning.Dlib.dll'
	)
	$roots = @(
		'C:\Program Files\Microsoft\ArtifactSigningClientTools',
		'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools',
		"$env:USERPROFILE\.nuget\packages\microsoft.trusted.signing.client",
		"$env:USERPROFILE\.nuget\packages\microsoft.artifactsigning.client"
	)
	foreach ($root in $roots) {
		$path = Find-FirstFile -Candidates $candidates -SearchRoot $root -LeafName 'Azure.CodeSigning.Dlib.dll'
		if ($path) { return $path }
	}
	throw "Azure.CodeSigning.Dlib.dll not found. Install the Artifact Signing Client Tools: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools"
}

function Assert-AzureLogin {
	$az = Get-Command az -ErrorAction SilentlyContinue
	if (-not $az) {
		throw "Azure CLI (az) not found. Install it and run 'az login' before signing."
	}
	& az account show 1>$null 2>$null
	if ($LASTEXITCODE -ne 0) {
		Write-Host "Not signed in to Azure. Launching 'az login'..." -ForegroundColor Yellow
		& az login | Out-Null
		if ($LASTEXITCODE -ne 0) { throw "az login failed. Authenticate and retry." }
	}
}

# --- Validate inputs ---
if (-not (Test-Path $FilePath)) {
	throw "File to sign not found: $FilePath. Run publish-tool.ps1 first."
}
$FilePath = (Resolve-Path $FilePath).Path

Write-Host "Locating signing tools..." -ForegroundColor Cyan
$signtool = Resolve-SignTool
$dlib = Resolve-Dlib
Write-Host "  signtool: $signtool" -ForegroundColor DarkGray
Write-Host "  dlib:     $dlib" -ForegroundColor DarkGray

Assert-AzureLogin

# --- Write transient metadata.json ---
$signingDir = Join-Path $PSScriptRoot 'signing'
if (-not (Test-Path $signingDir)) { New-Item -ItemType Directory -Path $signingDir -Force | Out-Null }
$metadataPath = Join-Path $signingDir 'metadata.json'

$normalizedEndpoint = if ($Endpoint.EndsWith('/')) { $Endpoint } else { "$Endpoint/" }
[ordered]@{
	Endpoint               = $normalizedEndpoint
	CodeSigningAccountName = $AccountName
	CertificateProfileName = $CertificateProfileName
} | ConvertTo-Json | Set-Content -Path $metadataPath -Encoding utf8
Write-Host "Wrote metadata: $metadataPath" -ForegroundColor DarkGray

# --- Sign ---
Write-Host "Signing '$([System.IO.Path]::GetFileName($FilePath))'..." -ForegroundColor Cyan
& $signtool sign /v /debug /fd SHA256 `
	/tr $TimestampUrl /td SHA256 `
	/dlib $dlib `
	/dmdf $metadataPath `
	$FilePath

if ($LASTEXITCODE -ne 0) {
	Write-Host ""
	Write-Host "Signing failed (exit $LASTEXITCODE). Common causes:" -ForegroundColor Red
	Write-Host "  403 / SignerSign() failed:" -ForegroundColor Yellow
	Write-Host "   - AccountName must be the Trusted Signing ACCOUNT name (not user/email/app id)." -ForegroundColor Yellow
	Write-Host "   - Endpoint region must match where the account + profile were created." -ForegroundColor Yellow
	Write-Host "   - Signing identity needs the 'Trusted Signing Certificate Profile Signer' role." -ForegroundColor Yellow
	Write-Host "   - Identity validation status must be 'Completed' (not on a Trial subscription)." -ForegroundColor Yellow
	Write-Host "   - Install latest VC++ redist and ensure .NET 8 runtime is present." -ForegroundColor Yellow
	throw "signtool sign failed."
}

# --- Verify ---
Write-Host "Verifying signature..." -ForegroundColor Cyan
& $signtool verify /pa /v $FilePath
if ($LASTEXITCODE -ne 0) { throw "Signature verification failed." }

Write-Host ""
Write-Host "Successfully signed and verified: $FilePath" -ForegroundColor Green
