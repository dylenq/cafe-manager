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
    [switch]$NoPause,

    # Set by the .cmd launcher, which keeps its own window open; the script then skips its final pause there.
    [switch]$FromLauncher
)

# Captured at script scope: inside a function $PSBoundParameters would be the function's own (empty) set.
$ScriptArguments = $PSBoundParameters

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# The administrator run keeps a log next to this script, so a failure can still be read after its window closes.
$LogPath = $null
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        Start-Transcript -LiteralPath (Join-Path $PSScriptRoot 'uninstall-server.log') -Append -ErrorAction Stop | Out-Null
        $LogPath = Join-Path $PSScriptRoot 'uninstall-server.log'
    }
    catch {
        $LogPath = $null
    }
}

$ServiceName = 'CafeManager'
$FirewallRuleName = 'Cafe Manager (TCP 5080)'
$DataPath = Join-Path $env:ProgramData 'CafeManager'

function Wait-ForEnter {
    if (-not $NoPause -and -not $FromLauncher) {
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

# robocopy /MIR and Remove-Item -Recurse delete whatever is in the target folder, so refuse any -InstallPath that is
# not clearly a CafeManager folder: it must be a full path with a drive letter, contain a folder named CafeManager
# (for example C:\Program Files\CafeManager\Server), and must not be a drive root or a Windows system folder.
function Assert-SafeInstallPath {
    param([string]$Path)
    $example = 'C:\Program Files\CafeManager\Server'
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:\\') {
        throw "-InstallPath must be a full path that starts with a drive letter, for example $example. Got: '$Path'"
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $segments = @($full.Split('\') | Where-Object { $_ -ne '' })
    if ($segments.Count -lt 2) {
        throw "-InstallPath '$Path' is a drive root. Use a CafeManager folder, for example $example."
    }
    if ($segments -notcontains 'CafeManager') {
        throw "-InstallPath '$Path' is not inside a folder named CafeManager. Use for example $example."
    }
    $protected = @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, ($env:SystemDrive + '\Users'), $env:USERPROFILE)
    foreach ($item in $protected) {
        if ([string]::IsNullOrWhiteSpace($item)) { continue }
        if ($full -ieq $item.TrimEnd('\')) {
            throw "-InstallPath '$Path' is a system folder. Use a CafeManager folder, for example $example."
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
        $windowsPrefix = $env:SystemRoot.TrimEnd('\') + '\'
        if ($full.StartsWith($windowsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "-InstallPath '$Path' is inside the Windows folder. Use for example $example."
        }
    }
}

# An elevated (administrator) window runs in a separate Windows logon session that cannot see mapped network drives,
# so a script started from one would close at once without a message. Refuse that case up front.
function Assert-LocalScriptFolder {
    $folder = $PSScriptRoot
    $isNetwork = $folder.StartsWith('\\')
    if (-not $isNetwork) {
        try {
            $drive = New-Object System.IO.DriveInfo([System.IO.Path]::GetPathRoot($folder))
            $isNetwork = ($drive.DriveType -eq [System.IO.DriveType]::Network)
        }
        catch {
            $isNetwork = $false
        }
    }
    if ($isNetwork) {
        throw "This folder is on a network drive ($folder), which the administrator window cannot open. Copy the extracted folder to this PC first, for example to C:\CafeManager-Setup, and run it from there."
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Uninstall-Server {
    $InstallPath = Remove-TrailingBackslash $InstallPath
    Assert-SafeInstallPath -Path $InstallPath
    if (-not (Test-IsAdministrator)) {
        Write-Host 'Administrator rights are needed. A Windows (UAC) prompt will appear: choose Yes.'
        # The elevated window must not inherit -FromLauncher: nothing else would keep that window open.
        [void]$ScriptArguments.Remove('FromLauncher')
        Assert-LocalScriptFolder
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
        $code = $child.ExitCode
        Write-Host ''
        if ($null -eq $code) {
            Write-Host 'The administrator window has closed.'
            $code = 0
        }
        elseif ($code -eq 0) {
            Write-Host 'The administrator window finished.' -ForegroundColor Green
        }
        else {
            Write-Host "The administrator window stopped with exit code $code. Its messages explain why." -ForegroundColor Red
        }
        exit $code
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
    # Only tidy up the shared CafeManager folder, never an arbitrary parent (a drive root or an unrelated folder).
    if ($parent -and ((Split-Path -Leaf $parent) -ieq 'CafeManager') -and (Test-Path -LiteralPath $parent)) {
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
    if ($LogPath) { Write-Host "A log of this run is in $LogPath" }
    Wait-ForEnter
    exit 1
}
