<#
.SYNOPSIS
    Removes the Cafe Manager agent from a gaming PC.

.DESCRIPTION
    Stops and deletes the "CafeManager Agent" scheduled task, stops the agent, removes the Task Manager restriction
    (DisableTaskMgr) for the gaming user and deletes the agent program folder. The agent's data in
    C:\ProgramData\CafeManager\agent (emergency lockout state) is KEPT unless you pass -RemoveData.

.PARAMETER GamingUser
    The Windows account that plays on this PC, as user or DOMAIN\user. Default: the user running this script.

.PARAMETER InstallPath
    Where the agent was installed. Default: C:\Program Files\CafeManager\Agent.

.PARAMETER RemoveData
    Also delete C:\ProgramData\CafeManager\agent.

.PARAMETER NoPause
    Do not wait for Enter at the end (for unattended runs).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall-agent.ps1
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GamingUser = '',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'CafeManager\Agent'),
    [switch]$RemoveData,
    [switch]$NoPause
)

# Captured at script scope: inside a function $PSBoundParameters would be the function's own (empty) set.
$ScriptArguments = $PSBoundParameters

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$TaskName = 'CafeManager Agent'
$ProcessName = 'Cafe.Agent'
$TempHiveName = 'CafeManagerTemp'
$PolicyKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'
$AgentDataPath = Join-Path $env:ProgramData 'CafeManager\agent'

# Capture the invoking user BEFORE elevation and pass it through.
if ([string]::IsNullOrWhiteSpace($GamingUser)) {
    $GamingUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $ScriptArguments['GamingUser'] = $GamingUser
}

function Wait-ForEnter {
    if (-not $NoPause) {
        [void](Read-Host 'Press Enter to close this window')
    }
}

# Quotes one value for a native program command line (CommandLineToArgvW rules): backslashes before a quote are
# doubled and the quote is escaped, and trailing backslashes are doubled before the closing quote.
function Quote-NativeArg {
    param([string]$Value)
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

# A trailing backslash on a folder parameter would be read as an escaped quote by native programs (robocopy), so drop
# it (a bare drive root such as C:\ is left alone; the install-path check rejects those later anyway).
function Remove-TrailingBackslash {
    param([string]$Path)
    if ($Path.Length -gt 3) { return $Path.TrimEnd('\') }
    return $Path
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-UserSid {
    param([string]$Account)
    try {
        $nt = New-Object System.Security.Principal.NTAccount($Account)
        return $nt.Translate([System.Security.Principal.SecurityIdentifier])
    }
    catch {
        return $null
    }
}

function Invoke-RegExe {
    # Runs reg.exe and returns its exit code. In Windows PowerShell 5.1 a native command's stderr output becomes a
    # terminating error when $ErrorActionPreference is 'Stop' and the stream is redirected, so relax it for the call
    # and judge success by the exit code only.
    param([string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & reg.exe @Arguments *> $null
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-WithUserHive {
    # Runs $Action with the registry root of the user's hive: HKEY_USERS\<SID> when loaded, else the user's
    # NTUSER.DAT temporarily loaded. Returns $false when the user has no profile.
    param([string]$Sid, [scriptblock]$Action)

    if (Test-Path -LiteralPath "Registry::HKEY_USERS\$Sid") {
        & $Action "Registry::HKEY_USERS\$Sid"
        return $true
    }

    $profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid"
    $profilePath = $null
    if (Test-Path -LiteralPath $profileKey) {
        $profilePath = (Get-ItemProperty -LiteralPath $profileKey).ProfileImagePath
    }
    if ([string]::IsNullOrWhiteSpace($profilePath)) { return $false }
    $profilePath = [Environment]::ExpandEnvironmentVariables($profilePath)
    $hiveFile = Join-Path $profilePath 'NTUSER.DAT'
    if (-not (Test-Path -LiteralPath $hiveFile)) { return $false }

    $loadExit = Invoke-RegExe -Arguments @('load', "HKU\$TempHiveName", $hiveFile)
    if ($loadExit -ne 0) {
        throw "Could not load the registry hive $hiveFile (exit code $loadExit). Is the user logging on right now?"
    }
    try {
        & $Action "Registry::HKEY_USERS\$TempHiveName"
    }
    finally {
        # Always try to unload (also when the action threw). Open registry handles from the action can make the first
        # attempts fail with "access denied", so collect garbage, wait and retry. This block must never throw.
        $unloaded = $false
        try {
            for ($attempt = 1; $attempt -le 5 -and -not $unloaded; $attempt++) {
                [GC]::Collect()
                [GC]::WaitForPendingFinalizers()
                if ((Invoke-RegExe -Arguments @('unload', "HKU\$TempHiveName")) -eq 0) {
                    $unloaded = $true
                }
                else {
                    Start-Sleep -Seconds 2
                }
            }
        }
        catch {
            Write-Verbose ('Unloading the temporary registry hive failed: ' + $_.Exception.Message)
        }
        if (-not $unloaded) {
            Write-Warning "The temporary registry hive HKU\$TempHiveName could not be unloaded. Restart the PC or run: reg.exe unload HKU\$TempHiveName"
        }
    }
    return $true
}

function Remove-TaskManagerPolicy {
    param([string]$Sid)
    $found = Invoke-WithUserHive -Sid $Sid -Action {
        param($Root)
        $key = Join-Path $Root $PolicyKeyPath
        if (Test-Path -LiteralPath $key) {
            Remove-ItemProperty -LiteralPath $key -Name 'DisableTaskMgr' -ErrorAction SilentlyContinue
        }
    }
    return $found
}

function Remove-FolderWithRetry {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

function Uninstall-Agent {
    $InstallPath = Remove-TrailingBackslash $InstallPath
    if (-not (Test-IsAdministrator)) {
        Write-Host 'Administrator rights are needed. A Windows (UAC) prompt will appear: choose Yes.'
        $argList = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-NativeArg $PSCommandPath)
        foreach ($key in $ScriptArguments.Keys) {
            $value = $ScriptArguments[$key]
            if ($value -is [System.Management.Automation.SwitchParameter]) {
                if ($value.IsPresent) { $argList += ' -' + $key }
            }
            else {
                $argList += ' -' + $key + ' ' + (Quote-NativeArg ([string]$value))
            }
        }
        $child = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $child.ExitCode
    }

    Write-Host '== Cafe Manager agent uninstall ==' -ForegroundColor Cyan

    Write-Host "[1/5] Stopping and removing the scheduled task '$TaskName'"
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    else {
        Write-Host '      The task was not registered.'
    }

    Write-Host '[2/5] Stopping the agent'
    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }

    Write-Host "[3/5] Restoring Task Manager for '$GamingUser'"
    $sid = Resolve-UserSid -Account $GamingUser
    if ($null -eq $sid) {
        Write-Warning "The account '$GamingUser' was not found. The Task Manager policy was not touched."
    }
    else {
        $found = Remove-TaskManagerPolicy -Sid $sid.Value
        if (-not $found) {
            Write-Host '      That user has no profile on this PC: nothing to restore.'
        }
    }

    Write-Host "[4/5] Removing the program files in $InstallPath"
    Remove-FolderWithRetry -Path $InstallPath
    $parent = Split-Path -Parent $InstallPath
    if ($parent -and (Test-Path -LiteralPath $parent)) {
        $remaining = @(Get-ChildItem -LiteralPath $parent -Force)
        if ($remaining.Count -eq 0) {
            Remove-Item -LiteralPath $parent -Force
        }
    }

    Write-Host '[5/5] Data'
    if ($RemoveData) {
        Remove-FolderWithRetry -Path $AgentDataPath
        Write-Host "      Removed $AgentDataPath"
    }
    else {
        Write-Host "      Kept $AgentDataPath (pass -RemoveData to delete it)."
    }

    Write-Host 'Done.' -ForegroundColor Green
}

try {
    Uninstall-Agent
    Wait-ForEnter
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    Wait-ForEnter
    exit 1
}
