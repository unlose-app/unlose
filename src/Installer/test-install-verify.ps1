# Installer verification helper: silent-install regression + status check.
# Usage: powershell -File test-install-verify.ps1 [-Interactive]
param([switch]$Interactive)

$msi = Join-Path $PSScriptRoot '..\..\publish\unlose-setup-x64.msi'
$msi = (Resolve-Path $msi).Path

if ($Interactive) {
    # Full-UI install: shows LanguageDlg -> progress -> ExitDlg (with checkbox), RunAs for elevation
    Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`""
    Write-Output 'interactive install launched (UAC prompt + LanguageDlg should appear)'
    exit
}

$p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn' -Verb RunAs -Wait -PassThru
Write-Output ("msiexec exit: " + $p.ExitCode)
Start-Sleep 3
$svc = Get-Service unloseService -ErrorAction SilentlyContinue
Write-Output ("service: " + $(if ($svc) { $svc.Status } else { 'NOT FOUND' }))
$ui = Get-Process unlose.UI -ErrorAction SilentlyContinue
Write-Output ("UI process count after silent install (expect 0): " + $(if ($ui) { @($ui).Count } else { 0 }))
