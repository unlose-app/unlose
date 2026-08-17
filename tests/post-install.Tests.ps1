#Requires -RunAsAdministrator
<#
.SYNOPSIS
    unlose post-install smoke tests (Pester v5)
.DESCRIPTION
    Verifies that after MSI install the service is registered, running, the CLI is callable,
    pipe communication works, and basic snapshot operations succeed.
    Must run as administrator.
.PARAMETER InstallDir
    unlose install directory, default "C:\Program Files\unlose"
#>
param(
    [string]$InstallDir = "C:\Program Files\unlose"
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
BeforeAll {
    # Pester 5 container isolation: top-level variables must use script: scope to be visible inside It blocks
    $script:ServiceName = 'unloseService'
    $script:CliBin      = Join-Path $InstallDir 'unlose.exe'
    # Ensure the CLI is callable via PATH; otherwise use the absolute path
    if (-not (Get-Command unlose -ErrorAction SilentlyContinue)) {
        $env:PATH = "$InstallDir;$env:PATH"
    }
}

# ---------------------------------------------------------------------------
Describe "1. Installation integrity" {

    It "Install directory exists" {
        Test-Path $InstallDir -PathType Container | Should -BeTrue
    }

    It "unlose.UI.exe exists" {
        Test-Path (Join-Path $InstallDir 'unlose.UI.exe') | Should -BeTrue
    }

    It "unlose.Service.exe exists" {
        Test-Path (Join-Path $InstallDir 'unlose.Service.exe') | Should -BeTrue
    }

    It "unlose.exe exists" {
        Test-Path $CliBin | Should -BeTrue
    }
}

# ---------------------------------------------------------------------------
Describe "2. Windows service" {

    It "Service is registered with the SCM" {
        $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
        $svc | Should -Not -BeNull
    }

    It "Service start type is Automatic" {
        $svc = Get-WmiObject Win32_Service -Filter "Name='$ServiceName'"
        $svc.StartMode | Should -Be 'Auto'
    }

    It "Service status is Running" {
        $svc = Get-Service $ServiceName
        $svc.Status | Should -Be 'Running'
    }
}

# ---------------------------------------------------------------------------
Describe "3. CLI basic commands" {

    It "unlose status returns exit code 0" {
        & $CliBin status | Out-Null
        $LASTEXITCODE | Should -Be 0
    }

    It "unlose status output contains the IsPaused field" {
        $out = & $CliBin status
        $out | Should -Match 'IsPaused'
    }

    It "unlose list-snapshots returns exit code 0" {
        & $CliBin list-snapshots | Out-Null
        $LASTEXITCODE | Should -Be 0
    }

    It "Invalid command returns a non-zero exit code" {
        & $CliBin invalid-command-xyz 2>&1 | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }
}

# ---------------------------------------------------------------------------
Describe "4. Pipe communication & snapshot operations" {

    It "Creating a snapshot (C:\) succeeds" {
        $out = & $CliBin create-snapshot --volume C:\
        $LASTEXITCODE | Should -Be 0
        # CLI output format: "Snapshot created: {guid}  at {time}  label=..."
        # Extract the GUID from it (CLI HandleSnapshotAsync formatted output)
        $out | Should -Match 'Snapshot created:'
        $out | Should -Match '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
    }

    It "Listing snapshots returns at least 1 record" {
        $out = & $CliBin list-snapshots
        $LASTEXITCODE | Should -Be 0
        # Response is a JSON array with at least one element
        $items = $out | ConvertFrom-Json -ErrorAction SilentlyContinue
        $items.Count | Should -BeGreaterThan 0
    }

    It "Pause protection for 1 minute" {
        & $CliBin pause 1 | Out-Null
        $LASTEXITCODE | Should -Be 0

        $status = & $CliBin status
        $status | Should -Match 'IsPaused=True'
    }

    It "Resume protection" {
        & $CliBin resume | Out-Null
        $LASTEXITCODE | Should -Be 0

        $status = & $CliBin status
        $status | Should -Match 'IsPaused=False'
    }
}

# ---------------------------------------------------------------------------
Describe "5. Service crash recovery configuration" {

    It "SCM has a crash recovery policy configured (restart on 1st failure)" {
        $rec = sc.exe qfailure $ServiceName | Out-String
        $rec | Should -Match 'RESTART'
    }
}
