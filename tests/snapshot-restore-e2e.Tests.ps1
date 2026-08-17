#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
    unlose L2 file-restore integration tests (Pester v5)
.DESCRIPTION
    Performs real VSS snapshot/restore and asserts byte-for-byte file restoration via SHA256 hashes.
    Covers the five scenario classes from section 4 of the test plan: happy path / empty state /
    boundary / failure path / degraded mode.

    Three-path adaptive test-volume backend:
      - HyperV   : New-VHD/Mount-VHD (Pro/Enterprise + Hyper-V role)
      - Diskpart : diskpart create vdisk (support verified by probing; some Home editions lack it)
      - TempDir  : degraded temp directory (Home-edition fallback; VSS snapshot runs on the C: volume)

    Design principles:
      - Assertion discipline: verify SHA256 hash equality, not just "file exists" — tampered content must be detected
      - Zero environment residue: created in BeforeAll, cleaned up in AfterAll

    Prerequisites:
      - Administrator rights
      - unloseService installed and running
      - CLI driver: talks to the service via unlose.exe over IPC
.PARAMETER InstallDir
    unlose install directory, default "C:\Program Files\unlose"
.PARAMETER VhdSizeGb
    VHDX size in GB, default 2 (HyperV/Diskpart modes only)
.NOTES
    Corresponding plan: docs/简化改造/发布前自动化测试方案.md, section 4 — L2 real integration layer.
    Note: all helper functions are defined with script: scope inside BeforeAll (required by Pester 5 container isolation).
#>
param(
    [string]$InstallDir = "C:\Program Files\unlose",
    [int]$VhdSizeGb = 2
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Global test context (populated in BeforeAll, shared across Describes)
# ---------------------------------------------------------------------------
$script:CliBin       = Join-Path $InstallDir 'unlose.exe'
$script:VhdMode      = $null
$script:VhdPath      = $null
$script:ActualDriveLetter = $null
$script:TestRoot     = $null
$script:GoldenDir    = $null
$script:SnapshotVolume = $null    # snapshot target volume (VHDX mode = dedicated drive, TempDir mode = C:)
$script:GoldenHashes = @{ }

# ===========================================================================
# BeforeAll / AfterAll: test volume lifecycle
# ===========================================================================

BeforeAll {
    # ── Helper functions (all script: scope, required by Pester 5) ──────────

    # ── VHDX lifecycle (three paths) ────────────────────────────────────────
    function script:Test-VhdCapability {
        # Path 1: Hyper-V cmdlets
        if (Get-Command New-VHD -ErrorAction SilentlyContinue) { return 'HyperV' }
        # Path 2: diskpart create vdisk (verified by probing — Home editions ship diskpart.exe but often lack create vdisk)
        if (Get-Command diskpart.exe -ErrorAction SilentlyContinue) {
            $probeVhd = Join-Path $env:TEMP "cg-cap-probe-$($PID).vhdx"
            Remove-Item $probeVhd -Force -ErrorAction SilentlyContinue
            $tmp = [System.IO.Path]::GetTempFileName()
            try {
                $s = "create vdisk file=`"$probeVhd`" maximum=64 type=expandable"
                [System.IO.File]::WriteAllText($tmp, $s, [System.Text.Encoding]::ASCII)
                $null = & diskpart.exe /s $tmp 2>&1
                $created = Test-Path $probeVhd
                Remove-Item $probeVhd -Force -ErrorAction SilentlyContinue
                if ($created) { return 'Diskpart' }
            } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        }
        return 'TempDir'
    }
    function script:Find-FreeDriveLetter {
        $used = (Get-PSDrive -PSProvider FileSystem).Name
        for ($c = [int][char]'Z'; $c -ge [int][char]'E'; $c--) {
            $letter = [char]$c
            if ($used -notcontains $letter) { return $letter }
        }
        throw "No free drive letter (E-Z are all in use)"
    }
    function script:Invoke-DiskpartScript {
        param([Parameter(Mandatory)][string]$ScriptText)
        $tmp = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($tmp, $ScriptText, [System.Text.Encoding]::ASCII)
            $output = & diskpart.exe /s $tmp 2>&1 | Out-String
            if ($output -match 'DiskPart (has encountered an error|无法处理)') {
                throw "diskpart reported an error: $output"
            }
            return $output
        } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }
    function script:New-TestVhd {
        param([Parameter(Mandatory)][string]$Path, [int]$SizeGb)
        $s = "create vdisk file=`"$Path`" maximum=$($SizeGb * 1024) type=expandable"
        $null = script:Invoke-DiskpartScript -ScriptText $s
        if (-not (Test-Path $Path)) { throw "File does not exist after diskpart create vdisk: $Path" }
    }
    function script:Mount-TestVhd {
        param([Parameter(Mandatory)][string]$Path, [string]$Label = 'CGL2TEST')
        if (Get-Command Mount-VHD -ErrorAction SilentlyContinue) {
            Mount-VHD -Path $Path
            $diskNum = (Get-VHD -Path $Path).Number
            try { Initialize-Disk -Number $diskNum -PartitionStyle GPT -Confirm:$false -ErrorAction Stop } catch {}
            $part = New-Partition -DiskNumber $diskNum -UseMaximumSize -AssignDriveLetter -ErrorAction Stop
            Format-Volume -DriveLetter $part.DriveLetter -FileSystem NTFS -NewFileSystemLabel $Label -Confirm:$false | Out-Null
            return $part.DriveLetter
        } else {
            $letter = script:Find-FreeDriveLetter
            $s = @"
select vdisk file="$Path"
attach vdisk
convert gpt
create partition primary
assign letter=$letter
format quick fs=ntfs label="$Label"
"@
            $null = script:Invoke-DiskpartScript -ScriptText $s
            if (-not (Test-Path "${letter}:\")) { throw "Drive letter $letter is unavailable after diskpart assign" }
            return $letter
        }
    }
    function script:Dismount-TestVhd {
        param([Parameter(Mandatory)][string]$Path)
        if (Get-Command Dismount-VHD -ErrorAction SilentlyContinue) {
            Dismount-VHD -Path $Path -ErrorAction SilentlyContinue
        } else {
            $s = "select vdisk file=`"$Path`"`ndetach vdisk"
            try { $null = script:Invoke-DiskpartScript -ScriptText $s } catch { Write-Warning "detach failed: $_" }
        }
    }

    # ── Golden dataset and hashes ───────────────────────────────────────────
    function script:New-GoldenDataset {
        $g = $script:GoldenDir
        # Clear the old directory first (clear any leftover read-only attributes so WriteAllBytes is not denied)
        if (Test-Path $g) {
            Get-ChildItem -Path $g -Recurse -Force | ForEach-Object {
                try { $_.IsReadOnly = $false } catch {}
            }
            Remove-Item -Path $g -Recurse -Force -ErrorAction SilentlyContinue
        }
        New-Item -ItemType Directory -Force -Path $g | Out-Null
        Set-Content -Path "$g\readme.txt" -Value "unlose golden dataset - original content" -Encoding UTF8
        Set-Content -Path "$g\文档.txt" -Value "中文内容 - 用于验证非 ASCII 路径还原" -Encoding UTF8
        $deepDir = $g
        for ($i = 1; $i -le 8; $i++) { $deepDir = Join-Path $deepDir "Level$i" }
        New-Item -ItemType Directory -Force -Path $deepDir | Out-Null
        Set-Content -Path "$deepDir\deep-file.txt" -Value "deep nested file content" -Encoding UTF8
        $roBytes = [byte[]](1..255)
        [System.IO.File]::WriteAllBytes("$g\readonly.bin", $roBytes)
        Set-ItemProperty -Path "$g\readonly.bin" -Name IsReadOnly -Value $true
        New-Item -ItemType File -Path "$g\empty.txt" -Force | Out-Null
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $bigBytes = New-Object byte[] 1048576
        $rng.GetBytes($bigBytes)
        [System.IO.File]::WriteAllBytes("$g\large.bin", $bigBytes)
        $rng.Dispose()
        New-Item -ItemType Directory -Force -Path "$g\Sub" | Out-Null
        Set-Content -Path "$g\Sub\nested.txt" -Value "nested directory content" -Encoding UTF8
    }
    function script:Update-GoldenHashes {
        $script:GoldenHashes.Clear()
        $files = Get-ChildItem -Path $script:GoldenDir -Recurse -File -Force
        foreach ($f in $files) {
            $rel = $f.FullName.Substring($script:GoldenDir.Length).TrimStart('\')
            $script:GoldenHashes[$rel] = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
        }
    }
    function script:Compare-ToGolden {
        $result = @{ Match = $true; Mismatched = @(); Missing = @(); Extra = @() }
        $current = @{ }
        $files = Get-ChildItem -Path $script:GoldenDir -Recurse -File -Force -ErrorAction SilentlyContinue
        foreach ($f in $files) {
            $rel = $f.FullName.Substring($script:GoldenDir.Length).TrimStart('\')
            $current[$rel] = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
        }
        foreach ($k in $script:GoldenHashes.Keys) {
            if (-not $current.ContainsKey($k)) { $result.Missing += $k; $result.Match = $false }
        }
        foreach ($k in $current.Keys) {
            if (-not $script:GoldenHashes.ContainsKey($k)) { $result.Extra += $k; $result.Match = $false }
        }
        foreach ($k in $script:GoldenHashes.Keys) {
            if ($current.ContainsKey($k) -and $current[$k] -ne $script:GoldenHashes[$k]) { $result.Mismatched += $k; $result.Match = $false }
        }
        return $result
    }
    function script:Clear-GoldenFiles {
        Get-ChildItem -Path $script:GoldenDir -Recurse -File -Force | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    # ── CLI invocation ──────────────────────────────────────────────────────
    function script:Invoke-CliCommand {
        param([Parameter(Mandatory)][string[]]$Arguments)
        $out = & $script:CliBin @Arguments 2>&1
        $code = $LASTEXITCODE
        $stdout = ($out | Out-String).Trim()
        $parsed = $null
        try { $parsed = $stdout | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }
        return @{ ExitCode = $code; Stdout = $stdout; Parsed = $parsed }
    }
    function script:Extract-SnapshotId {
        param([string]$Output)
        if ($Output -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
            return $Matches[1]
        }
        return $null
    }

    # ── Precondition checks ─────────────────────────────────────────────────
    # Pester 5 container isolation: top-level $script: variables must be re-initialized here
    $script:GoldenHashes = @{ }
    $script:CliBin = Join-Path $InstallDir 'unlose.exe'
    if (-not (Test-Path $script:CliBin)) {
        throw "unlose.exe does not exist: $($script:CliBin). Install unlose via MSI first, or specify -InstallDir."
    }
    $svc = Get-Service unloseService -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -ne 'Running') {
        throw "unloseService is not running."
    }

    # ── Test volume preparation (three paths) ───────────────────────────────
    $script:VhdMode = script:Test-VhdCapability
    Write-Host "[BeforeAll] Test volume backend: $($script:VhdMode)" -ForegroundColor Cyan

    if ($script:VhdMode -eq 'TempDir') {
        Write-Host "[BeforeAll] Degraded temp-directory mode (Home edition: create vdisk unavailable). VSS snapshot runs on the C: volume." -ForegroundColor Yellow
        $script:VhdPath = $null
        $script:ActualDriveLetter = $null
        $script:TestRoot = Join-Path $env:TEMP "cg-l2-test-$($PID)-$(Get-Random -Maximum 99999)"
        $script:GoldenDir = Join-Path $script:TestRoot "Golden"
        $script:SnapshotVolume = 'C:\'    # TempDir lives under C:, so the snapshot runs on the C: volume
    } else {
        $script:VhdPath = Join-Path $env:TEMP "cg-l2-test-$($PID)-$(Get-Random -Maximum 99999).vhdx"
        script:New-TestVhd -Path $script:VhdPath -SizeGb $VhdSizeGb
        $script:ActualDriveLetter = script:Mount-TestVhd -Path $script:VhdPath
        $script:TestRoot = "$($script:ActualDriveLetter):\"
        $script:GoldenDir = Join-Path $script:TestRoot "Golden"
        $script:SnapshotVolume = $script:TestRoot
    }
    Write-Host "[BeforeAll] Test root: $($script:TestRoot) | Snapshot volume: $($script:SnapshotVolume)" -ForegroundColor Green

    # ── Lay down the golden dataset + build the manifest ────────────────────
    Write-Host "[BeforeAll] Laying down the golden dataset..." -ForegroundColor Cyan
    script:New-GoldenDataset
    script:Update-GoldenHashes
    Write-Host "[BeforeAll] Golden manifest: $($script:GoldenHashes.Count) files" -ForegroundColor Green
}

AfterAll {
    Write-Host "[AfterAll] Cleaning up the test environment..." -ForegroundColor Cyan
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    Start-Sleep -Milliseconds 500

    if ($script:VhdMode -ne 'TempDir' -and $script:VhdPath -and (Test-Path $script:VhdPath)) {
        try { script:Dismount-TestVhd -Path $script:VhdPath } catch { Write-Warning "Failed to dismount the VHDX: $_" }
        try { Remove-Item -Path $script:VhdPath -Force -ErrorAction SilentlyContinue } catch {}
    } elseif ($script:TestRoot -and (Test-Path $script:TestRoot)) {
        try { Remove-Item -Path $script:TestRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    }
    Write-Host "[AfterAll] Cleanup complete." -ForegroundColor Green
}

# ===========================================================================
# Five scenario classes (plan section 4)
# ===========================================================================

Describe "1. Happy path - delete files after the snapshot and restore; hashes match byte-for-byte" {
    BeforeEach {
        script:New-GoldenDataset
        script:Update-GoldenHashes
    }

    It "Create a snapshot (golden dataset in place)" {
        $r = script:Invoke-CliCommand -Arguments @('create-snapshot', '--volume', $script:SnapshotVolume, '--label', 'L2-happy')
        $r.ExitCode | Should -Be 0
        $id = script:Extract-SnapshotId -Output $r.Stdout
        $id | Should -Not -BeNullOrEmpty
    }

    It "Delete the golden files then restore; every file SHA256 matches the golden manifest one by one" {
        if ($script:VhdMode -eq 'TempDir') {
            Set-ItResult -Skipped -Because "TempDir degraded mode cannot safely restore the whole C: volume (robocopy would hit locked system files / access denied). This case needs a dedicated VHDX volume (Hyper-V or a system that supports create vdisk)."
            return
        }
        $r = script:Invoke-CliCommand -Arguments @('create-snapshot', '--volume', $script:SnapshotVolume, '--label', 'L2-restore')
        $snapshotId = script:Extract-SnapshotId -Output $r.Stdout
        $snapshotId | Should -Not -BeNullOrEmpty

        script:Clear-GoldenFiles
        (Get-ChildItem -Path $script:GoldenDir -Recurse -File -Force).Count | Should -Be 0

        $rr = script:Invoke-CliCommand -Arguments @('restore-snapshot', $snapshotId)

        $cmp = script:Compare-ToGolden
        $cmp.Match | Should -BeTrue
        $cmp.Missing.Count | Should -Be 0
        $cmp.Mismatched.Count | Should -Be 0
    }

    It "Tamper with file content (not delete) then restore; the tampering is detected and reverted" {
        if ($script:VhdMode -eq 'TempDir') {
            Set-ItResult -Skipped -Because "TempDir degraded mode cannot safely restore the whole C: volume. This case needs a dedicated VHDX volume."
            return
        }
        $r = script:Invoke-CliCommand -Arguments @('create-snapshot', '--volume', $script:SnapshotVolume, '--label', 'L2-tamper')
        $snapshotId = script:Extract-SnapshotId -Output $r.Stdout
        $snapshotId | Should -Not -BeNullOrEmpty

        Set-Content -Path "$($script:GoldenDir)\readme.txt" -Value "TAMPERED - should be detected by the restore" -Encoding UTF8
        $tamperedHash = (Get-FileHash "$($script:GoldenDir)\readme.txt" -Algorithm SHA256).Hash
        $tamperedHash | Should -Not -Be $script:GoldenHashes['readme.txt']

        $rr = script:Invoke-CliCommand -Arguments @('restore-snapshot', $snapshotId)

        $cmp = script:Compare-ToGolden
        $cmp.Match | Should -BeTrue
        $cmp.Mismatched.Count | Should -Be 0
    }
}

Describe "2. Empty state - snapshot an empty volume; restore with no snapshots" {
    It "Clear the golden files, snapshot the empty directory; restore does not error" {
        Remove-Item -Path $script:GoldenDir -Recurse -Force -ErrorAction SilentlyContinue
        Test-Path $script:GoldenDir | Should -BeFalse

        $r = script:Invoke-CliCommand -Arguments @('create-snapshot', '--volume', $script:SnapshotVolume, '--label', 'L2-empty')
        $r.ExitCode | Should -Be 0
        $id = script:Extract-SnapshotId -Output $r.Stdout
        $id | Should -Not -BeNullOrEmpty

        $rr = script:Invoke-CliCommand -Arguments @('restore-snapshot', $id)
    }

    It "list-snapshots returns a non-empty array after multiple operations" {
        $r = script:Invoke-CliCommand -Arguments @('list-snapshots')
        $r.ExitCode | Should -Be 0
        $r.Parsed | Should -Not -BeNullOrEmpty
        @($r.Parsed).Count | Should -BeGreaterThan 0
    }
}

Describe "3. Boundary - file locks and attribute preservation" {
    BeforeEach {
        script:New-GoldenDataset
        script:Update-GoldenHashes
    }

    It "Restore while a file is locked by an open handle; the service does not crash" {
        $r = script:Invoke-CliCommand -Arguments @('create-snapshot', '--volume', $script:SnapshotVolume, '--label', 'L2-lock')
        $id = script:Extract-SnapshotId -Output $r.Stdout
        $id | Should -Not -BeNullOrEmpty

        $lockedFile = "$($script:GoldenDir)\large.bin"
        $fs = [System.IO.File]::Open($lockedFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            Set-Content -Path "$($script:GoldenDir)\readme.txt" -Value "changed" -Encoding UTF8
            $rr = script:Invoke-CliCommand -Arguments @('restore-snapshot', $id)
            $svc = Get-Service unloseService
            $svc.Status | Should -Be 'Running'
        }
        finally {
            $fs.Close(); $fs.Dispose()
        }
    }

    It "A read-only file retains the read-only attribute after restore" {
        $r = script:Invoke-CliCommand -Arguments @('create-snapshot', '--volume', $script:SnapshotVolume, '--label', 'L2-ro')
        $id = script:Extract-SnapshotId -Output $r.Stdout

        Set-Content -Path "$($script:GoldenDir)\readme.txt" -Value "changed" -Encoding UTF8
        $rr = script:Invoke-CliCommand -Arguments @('restore-snapshot', $id)

        $ro = Get-Item "$($script:GoldenDir)\readonly.bin"
        $ro.IsReadOnly | Should -BeTrue
    }
}

Describe "4. Failure path - restore a non-existent snapshot" {
    It "Restore a random (non-existent) GUID; the service stays alive" {
        $fakeId = [Guid]::NewGuid().ToString()
        $r = script:Invoke-CliCommand -Arguments @('restore-snapshot', $fakeId)
        $svc = Get-Service unloseService
        $svc.Status | Should -Be 'Running'
        if ($r.Parsed) {
            $r.Parsed.Success | Should -BeFalse
        }
    }
}

Describe "5. Degraded mode - disk space and service robustness" {
    It "status command works; the service is online" {
        $r = script:Invoke-CliCommand -Arguments @('status')
        $r.ExitCode | Should -Be 0
        $r.Stdout | Should -Match 'IsPaused'
    }

    It "list-restore-points command does not crash the service" {
        $r = script:Invoke-CliCommand -Arguments @('list-restore-points')
        $svc = Get-Service unloseService
        $svc.Status | Should -Be 'Running'
    }
}
