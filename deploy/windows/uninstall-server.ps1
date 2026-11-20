<#
.SYNOPSIS
    Removes the Cafe Manager server service, its firewall rule and its program files.

.DESCRIPTION
    Stops and deletes the "CafeManager" Windows service, removes the firewall rule "Cafe Manager (TCP 5080)" and deletes
    the program folder. Your data (database, logs, backups in C:\ProgramData\CafeManager) is KEPT unless you pass
    -RemoveData, which asks you to type DELETE first (skip the question with -Force).

.PARAMETER InstallPath
    Where the server programs were installed. Default: C:\Program Files\CafeManager\Server.

.PARAMETER RemoveData
    Also delete C:\ProgramData\CafeManager (database, logs and backups). This cannot be undone.

.PARAMETER Force
    With -RemoveData: do not ask for the DELETE confirmation.

.PARAMETER NoPause
    Do not wait for Enter at the end (for unattended runs).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall-server.ps1
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'CafeManager\Server'),
    [switch]$RemoveData,
    [switch]$Force,
    [switch]$NoPause
)

# Captured at script scope: inside a function $PSBoundParameters would be the function's own (empty) set.
$ScriptArguments = $PSBoundParameters

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$ServiceName = 'CafeManager'
$FirewallRuleName = 'Cafe Manager (TCP 5080)'
$DataPath = Join-Path $env:ProgramData 'CafeManager'

function Wait-ForEnter {
    if (-not $NoPause) {
        [void](Read-Host 'Press Enter to close this window')
    }
}

function Quote-Arg {
    param([string]$Value)
    return '"' + ($Value -replace '"', '""') + '"'
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Uninstall-Server {
    if (-not (Test-IsAdministrator)) {
        Write-Host 'Administrator rights are needed. A Windows (UAC) prompt will appear: choose Yes.'
        $argList = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $PSCommandPath)
        foreach ($key in $ScriptArguments.Keys) {
            $value = $ScriptArguments[$key]
            if ($value -is [System.Management.Automation.SwitchParameter]) {
                if ($value.IsPresent) { $argList += ' -' + $key }
            }
            else {
                $argList += ' -' + $key + ' ' + (Quote-Arg ([string]$value))
            }
        }
        $child = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $child.ExitCode
    }

    Write-Host '== Cafe Manager server uninstall ==' -ForegroundColor Cyan

    Write-Host '[1/4] Stopping and deleting the service'
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        & sc.exe delete $ServiceName | Out-Null
        # 1060 = the service does not exist (already gone); 1072 = marked for deletion (finishes on its own).
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060 -and $LASTEXITCODE -ne 1072) {
            throw "sc.exe delete failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Write-Host '      The service was not installed.'
    }

    Write-Host '[2/4] Removing the firewall rule'
    $rules = Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue
    if ($null -ne $rules) {
        $rules | Remove-NetFirewallRule
    }

    Write-Host "[3/4] Removing the program files in $InstallPath"
    if (Test-Path -LiteralPath $InstallPath) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
    }
    $parent = Split-Path -Parent $InstallPath
    if ($parent -and (Test-Path -LiteralPath $parent)) {
        $remaining = @(Get-ChildItem -LiteralPath $parent -Force)
        if ($remaining.Count -eq 0) {
            Remove-Item -LiteralPath $parent -Force
        }
    }

    Write-Host '[4/4] Data'
    if ($RemoveData) {
        $confirmed = $Force.IsPresent
        if (-not $confirmed) {
            $answer = Read-Host "This permanently deletes the database, logs and backups in $DataPath. Type DELETE to continue"
            $confirmed = ($answer -ceq 'DELETE')
        }
        if ($confirmed) {
            if (Test-Path -LiteralPath $DataPath) {
                Remove-Item -LiteralPath $DataPath -Recurse -Force
            }
            Write-Host "      Removed $DataPath"
        }
        else {
            Write-Host "      Confirmation not given. The data was kept in $DataPath"
        }
    }
    else {
        Write-Host "      Kept your data in $DataPath (pass -RemoveData to delete it)."
    }

    Write-Host 'Done.' -ForegroundColor Green
}

try {
    Uninstall-Server
    Wait-ForEnter
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    Wait-ForEnter
    exit 1
}
