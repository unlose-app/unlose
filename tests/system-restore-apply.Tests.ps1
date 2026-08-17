#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
    unlose system restore APPLY end-to-end test (Pester v5, runs only inside a VirtualBox VM)
.DESCRIPTION
    Really triggers a Windows system restore (via unlose CLI -> IPC -> WMI SystemRestore.Restore)
    to verify that the APPLY path initiates the restore correctly after the ARCH-APPLY-001 fix.

    WARNING: This is the only test that really triggers a system restore. NEVER run it bare on a dev
    machine — it will roll back system state.
    Hard guard: executes only inside a VirtualBox VM (checks Win32_ComputerSystem.Model contains "VirtualBox").

    Two-phase design (a WMI Restore is only scheduled; the actual restore happens on reboot):
      Phase setup  : create restore point A -> plant markers -> create restore point B -> APPLY to A -> reboot
      Phase verify : after reboot, assert the registry marker was rolled back, the user document survived, the service is alive

    How to run:
      # setup phase (inside the VM, as administrator)
      Invoke-Pester -Path tests/system-restore-apply.Tests.ps1 -Parameters @{ Phase = 'setup' }
      # after the VM auto-reboots, run the verify phase
      Invoke-Pester -Path tests/system-restore-apply.Tests.ps1 -Parameters @{ Phase = 'verify' }

    Prerequisites (inside the VM):
      - VirtualBox Windows guest (Model contains "VirtualBox")
      - Administrator rights
      - unloseService installed and running
      - System Restore enabled (Enable-ComputerRestore -Drive C:\)
      - Strongly recommended: take a VirtualBox snapshot of the VM first, so a failed restore can be rolled back
.PARAMETER InstallDir
    unlose install directory, default "C:\Program Files\unlose"
.PARAMETER Phase
    Test phase: 'setup' (default) or 'verify'
.NOTES
    Corresponding fixes: ARCH-APPLY-001 (fire-and-forget) + ARCH-APPLY-002 (audit).
    Make sure the VM has a VirtualBox snapshot before running.
#>
param(
    [string]$InstallDir = "C:\Program Files\unlose",
    [Parameter(Mandatory = $false)]
    [ValidateSet('setup', 'verify')]
    [string]$Phase = 'setup'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Prerequisite detection + helper functions (Pester 5 container isolation: constants/functions
# must be defined with script: scope inside BeforeAll; top-level definitions are invisible in the Run phase)
# ---------------------------------------------------------------------------

BeforeAll {
    $script:CliBin = Join-Path $InstallDir 'unlose.exe'

    # Marker constants
    $script:RegKeyPath = 'HKLM:\SOFTWARE\CGApplyTest'
    $script:RegValueName = 'Marker'
    $script:RegValueBefore = 'BEFORE_RESTORE'
    $script:DocMarkerPath = 'C:\Users\Public\cg-apply-marker.txt'
    $script:DocMarkerContent = 'user doc - should survive system restore'

    function script:Invoke-CliCommand {
        param([Parameter(Mandatory)][string[]]$Arguments)
        $out = & $script:CliBin @Arguments 2>&1
        $code = $LASTEXITCODE
        $stdout = ($out | Out-String).Trim()
        $parsed = $null
        try { $parsed = $stdout | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }
        return @{ ExitCode = $code; Stdout = $stdout; Parsed = $parsed }
    }

    function script:Test-IsVirtualBoxVM {
        try {
            $model = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).Model
            return ($model -like '*VirtualBox*')
        } catch {
            return $false
        }
    }

    function script:Test-SystemRestoreEnabled {
        try { $null = Get-ComputerRestorePoint -ErrorAction Stop; return $true }
        catch { return $false }
    }

    function script:Get-LatestRestorePointSeq {
        try {
            $points = Get-ComputerRestorePoint -ErrorAction Stop
            $latest = $points | Sort-Object CreationTime -Descending | Select-Object -First 1
            return $latest.SequenceNumber
        } catch {
            return $null
        }
    }

    function script:Get-RestorePointByDescription {
        param([string]$DescriptionFragment)
        try {
            $points = Get-ComputerRestorePoint -ErrorAction Stop
            return $points | Where-Object { $_.Description -like "*$DescriptionFragment*" } |
                   Sort-Object CreationTime -Descending | Select-Object -First 1
        } catch {
            return $null
        }
    }

    $script:IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $script:IsVM = script:Test-IsVirtualBoxVM
    $svc = Get-Service unloseService -ErrorAction SilentlyContinue
    $script:ServiceRunning = ($null -ne $svc -and $svc.Status -eq 'Running')
    $script:SRenabled = script:Test-SystemRestoreEnabled
    $script:CliAvailable = Test-Path $script:CliBin
    $script:CanRun = $script:IsAdmin -and $script:IsVM -and $script:ServiceRunning -and $script:SRenabled -and $script:CliAvailable

    Write-Host "[BeforeAll] Phase=$Phase Admin=$($script:IsAdmin) VM=$($script:IsVM) Service=$($script:ServiceRunning) SR=$($script:SRenabled) Cli=$($script:CliAvailable) CanRun=$($script:CanRun)" -ForegroundColor Cyan

    if (-not $script:CanRun) {
        Write-Warning "Prerequisites not met; all tests will be skipped. See the detection results above for the reasons."
    }
}

# ===========================================================================
# Phase: setup — create restore points, plant markers, run APPLY, trigger reboot
# ===========================================================================

Describe "Phase [setup] - plant markers and trigger the system restore" -Tag 'setup' {

    BeforeEach {
        if (-not $script:CanRun) {
            Set-ItResult -Skip -Because "Prerequisites not met (requires VM + admin + service + SR enabled + cli)"
            return
        }
    }

    It "Create restore point A (baseline)" {
        $r = Invoke-CliCommand -Arguments @('create-restore-point', 'CG-apply-test-A')
        $r.Parsed.Success | Should -BeTrue
        $script:PointA = Get-RestorePointByDescription -DescriptionFragment 'CG-apply-test-A'
        $script:PointA | Should -Not -BeNull
        $script:SeqA = $script:PointA.SequenceNumber
        Write-Host "Restore point A: seq=$($script:SeqA)" -ForegroundColor Green
    }

    It "Plant the registry marker + user document marker" {
        # Registry marker (system restore should roll it back)
        if (-not (Test-Path $script:RegKeyPath)) {
            New-Item -Path $script:RegKeyPath -Force | Out-Null
        }
        Set-ItemProperty -Path $script:RegKeyPath -Name $script:RegValueName -Value $script:RegValueBefore -Type String

        # User document marker (system restore must NOT touch it — product narrative evidence)
        $script:DocMarkerContent | Out-File -FilePath $script:DocMarkerPath -Encoding UTF8

        # Verify both markers were planted
        (Get-ItemProperty -Path $script:RegKeyPath -Name $script:RegValueName).$($script:RegValueName) | Should -Be $script:RegValueBefore
        Test-Path $script:DocMarkerPath | Should -BeTrue
    }

    It "Create restore point B (after planting markers)" {
        $r = Invoke-CliCommand -Arguments @('create-restore-point', 'CG-apply-test-B')
        $r.Parsed.Success | Should -BeTrue
    }

    It "Disable the 24h throttle (to prevent consecutive creation from being rejected)" {
        $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
        Set-ItemProperty -Path $key -Name 'SystemRestorePointCreationFrequency' -Value 0 -Type DWord
        $val = (Get-ItemProperty -Path $key -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue).'SystemRestorePointCreationFrequency'
        $val | Should -Be 0
    }

    It "APPLY to restore point A returns Success=true (real result after the ARCH-APPLY-001 fix)" {
        $r = Invoke-CliCommand -Arguments @('apply-restore-point', "$($script:SeqA)")
        $r.Parsed.Success | Should -BeTrue
        Write-Host "APPLY scheduled to seq=$($script:SeqA). WMI Restore only schedules; the actual restore happens on reboot." -ForegroundColor Yellow
    }

    It "Trigger the VM reboot (in 15 seconds)" {
        Write-Host "`n========================================" -ForegroundColor Yellow
        Write-Host "APPLY scheduled successfully. The VM will reboot in 15 seconds to complete the system restore." -ForegroundColor Yellow
        Write-Host "After the reboot, re-run this test (Phase=verify) to validate the restore result:" -ForegroundColor Yellow
        Write-Host "  Invoke-Pester -Path tests/system-restore-apply.Tests.ps1 -Parameters @{ Phase = 'verify' }" -ForegroundColor Cyan
        Write-Host "========================================`n" -ForegroundColor Yellow
        Start-Sleep -Seconds 15
        # Actually reboot — the VM disconnects right after this command
        Restart-Computer -Force
    }
}

# ===========================================================================
# Phase: verify — validate the restore effect after reboot
# ===========================================================================

Describe "Phase [verify] - validate the system restore effect after reboot" -Tag 'verify' {

    BeforeEach {
        if (-not $script:CanRun) {
            Set-ItResult -Skip -Because "Prerequisites not met"
            return
        }
    }

    It "Registry marker has been rolled back (system restore took effect)" {
        # System restore should roll back the registry — the key should be absent or the value no longer BEFORE
        $val = $null
        if (Test-Path $script:RegKeyPath) {
            $val = (Get-ItemProperty -Path $script:RegKeyPath -Name $script:RegValueName -ErrorAction SilentlyContinue).$($script:RegValueName)
        }
        $val | Should -Not -Be $script:RegValueBefore
        Write-Host "Registry marker rolled back: val=$val" -ForegroundColor Green
    }

    It "User document marker survives (system restore does not touch personal files — product narrative evidence)" {
        Test-Path $script:DocMarkerPath | Should -BeTrue
        $content = Get-Content -Path $script:DocMarkerPath -Raw -ErrorAction SilentlyContinue
        $content | Should -BeLike "*$($script:DocMarkerContent)*"
        Write-Host "User document intact" -ForegroundColor Green
    }

    It "unloseService is alive after reboot" {
        $svc = Get-Service unloseService -ErrorAction SilentlyContinue
        $svc | Should -Not -BeNull
        $svc.Status | Should -Be 'Running'
    }

    It "Restore point A can still be listed (system restore does not delete restore point records)" {
        $pointA = Get-RestorePointByDescription -DescriptionFragment 'CG-apply-test-A'
        $pointA | Should -Not -BeNull
    }
}
