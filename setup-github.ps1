# GitHub Repository Setup Script
# Run this after installing GitHub CLI (gh): https://cli.github.com/
#
# Prerequisites:
# 1. Install GitHub CLI: winget install GitHub.cli
# 2. Authenticate: gh auth login
# 3. Run this script from the solution root

# Create private repository
gh repo create BulkPimRoleSettings --private --source=. --remote=origin --push

# Enable GitHub Pages (deploy from GitHub Actions)
gh api repos/{owner}/BulkPimRoleSettings/pages -X POST -f "build_type=workflow" 2>$null

Write-Host ""
Write-Host "Done! Your repository is set up." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Place your screenshots in docs/screenshots/"
Write-Host "   - screenshot-home.png"
Write-Host "   - screenshot-roles.png"
Write-Host "   - screenshot-settings.png"
Write-Host "2. Commit and push: git add -A && git commit -m 'Add screenshots' && git push"
Write-Host "3. Go to repo Settings > Pages and ensure 'GitHub Actions' is selected as source"
Write-Host ""
Write-Host "Your GitHub Pages site will be at:"
Write-Host "  https://<username>.github.io/BulkPimRoleSettings/"
