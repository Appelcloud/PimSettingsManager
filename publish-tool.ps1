<#
.SYNOPSIS
	Rebuilds the committed, self-contained PIMSettings Manager GUI into the root tool/ folder.

.DESCRIPTION
	Publishes the WinUI 3 app (src/BulkPimRoleSettings) as a self-contained win-x64 build
	and writes the runnable output to the repository-root tool/ directory. The published
	folder is committed so the GUI can be run without installing anything:

		.\tool\PIMSettings Manager.exe

.EXAMPLE
	pwsh -File .\publish-tool.ps1
#>

[CmdletBinding()]
param(
	[ValidateSet('win-x64', 'win-x86', 'win-arm64')]
	[string]$Runtime = 'win-x64',
	[string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\BulkPimRoleSettings\BulkPimRoleSettings.csproj'
$toolDir = Join-Path $root 'tool'
$platform = switch ($Runtime) {
	'win-x64' { 'x64' }
	'win-x86' { 'x86' }
	'win-arm64' { 'ARM64' }
}

if (Test-Path $toolDir) {
	Write-Host "Clearing existing tool/ output..." -ForegroundColor Gray
	Get-ChildItem $toolDir -Force | Remove-Item -Recurse -Force
}

Write-Host "Publishing self-contained $Runtime ($Configuration) to tool/ ..." -ForegroundColor Cyan
dotnet publish $project `
	-c $Configuration `
	-p:Platform=$platform `
	-p:PublishProfile=$Runtime `
	-p:PublishDir="$toolDir\"

if ($LASTEXITCODE -ne 0) {
	throw "Publish failed with exit code $LASTEXITCODE."
}

Write-Host "Done. Run the tool with: .\tool\PIMSettings Manager.exe" -ForegroundColor Green
