<#
.SYNOPSIS
    Installs or upgrades the Cafe Manager server as the "CafeManager" Windows service.

.DESCRIPTION
    Run this on the front-desk PC (the server PC) from the extracted release bundle. It:
      1. asks for administrator rights (a UAC prompt appears) if it does not have them,
      2. copies the published server to C:\Program Files\CafeManager\Server,
      3. keeps data, logs and backups in C:\ProgramData\CafeManager (restricted to SYSTEM and Administrators),
      4. creates or updates the service (automatic delayed start, restart on failure),
      5. opens firewall TCP 5080 for Private and Domain networks,
      6. starts the service, waits until it answers, prints the LAN addresses and opens http://localhost:5080.

    It is safe to run again: a re-run stops the service, copies the new files and starts it again. Your data in
    C:\ProgramData\CafeManager is never touched by the copy.

    First-run setup (choosing the admin password) must be done on this PC itself at http://localhost:5080.

.PARAMETER SourcePath
    Folder holding the published server (contains Cafe.Server.exe). Default: the "Server" folder next to this script.

.PARAMETER InstallPath
    Where the server programs are installed. Default: C:\Program Files\CafeManager\Server.

.PARAMETER NoBrowser
    Do not open http://localhost:5080 at the end.

.PARAMETER NoPause
    Do not wait for Enter at the end (for unattended runs).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install-server.ps1

    If Windows blocks the script because the zip came from the internet, right-click the zip, choose Properties,
    tick Unblock, press OK and extract it again, or run:  Get-ChildItem -Recurse . | Unblock-File
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SourcePath = '',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'CafeManager\Server'),
    [switch]$NoBrowser,
    [switch]$NoPause,

    # Set by the .cmd launcher, which keeps its own window open; the script then skips its final pause there.
    [switch]$FromLauncher
)

# Captured at script scope: inside a function $PSBoundParameters would be the function's own (empty) set.
$ScriptArguments = $PSBoundParameters

# $PSScriptRoot and $PSCommandPath can be empty in Windows PowerShell 5.1 (seen in param() defaults on a real PC), so
# resolve this script's own path once, at script scope, from $MyInvocation.
$ScriptPath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ScriptPath)) { $ScriptPath = $PSCommandPath }
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptDir = (Get-Location).ProviderPath
}
else {
    $ScriptDir = Split-Path -Parent $ScriptPath
}
if ([string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath = Join-Path $ScriptDir 'Server' }

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# The administrator run keeps a log next to this script, so a failure can still be read after its window closes.
$LogPath = $null
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        Start-Transcript -LiteralPath (Join-Path $ScriptDir 'install-server.log') -Append -ErrorAction Stop | Out-Null
        $LogPath = Join-Path $ScriptDir 'install-server.log'
    }
    catch {
        $LogPath = $null
    }
}

$ServiceName = 'CafeManager'
$ServiceDisplayName = 'Cafe Manager Server'
$FirewallRuleName = 'Cafe Manager (TCP 5080)'
$Port = 5080
$DataPath = Join-Path $env:ProgramData 'CafeManager'

# Files extracted from a zip downloaded from the internet carry a "blocked" mark; clear it on the release folder so
# the service program can start without a SmartScreen prompt for every file.
Get-ChildItem -LiteralPath $ScriptDir -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

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
    $folder = $ScriptDir
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

function Test-ServerUp {
    param([string]$Url)
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 5
        return ([int]$response.StatusCode -eq 200 -or [int]$response.StatusCode -eq 302)
    }
    catch {
        $ex = $_.Exception
        if ($ex.PSObject.Properties['Response'] -and $null -ne $ex.Response) {
            $code = [int]$ex.Response.StatusCode
            return ($code -eq 200 -or $code -eq 302)
        }
        return $false
    }
}

function Install-Server {
    $SourcePath = Remove-TrailingBackslash $SourcePath
    $InstallPath = Remove-TrailingBackslash $InstallPath
    Assert-SafeInstallPath -Path $InstallPath
    # 1. Elevation
    if (-not (Test-IsAdministrator)) {
        Write-Host 'Administrator rights are needed. A Windows (UAC) prompt will appear: choose Yes.'
        # The elevated window must not inherit -FromLauncher: nothing else would keep that window open.
        [void]$ScriptArguments.Remove('FromLauncher')
        Assert-LocalScriptFolder
        if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
            throw 'Could not find the path of this script. Open PowerShell as administrator in the extracted folder and run it from there.'
        }
        $argList = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-NativeArg $ScriptPath)
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

    Write-Host '== Cafe Manager server install ==' -ForegroundColor Cyan

    # 2. Validate the source
    Write-Host "[1/8] Checking the server files in $SourcePath"
    if (-not (Test-Path -LiteralPath (Join-Path $SourcePath 'Cafe.Server.exe'))) {
        throw "Cafe.Server.exe was not found in '$SourcePath'. Run this script from the extracted release folder, or pass -SourcePath."
    }

    # Mirroring a folder into itself (or into one of its parents) would delete the source files.
    $sourceFull = ([IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SourcePath).ProviderPath)).TrimEnd('\') + '\'
    $targetFull = ([IO.Path]::GetFullPath($InstallPath)).TrimEnd('\') + '\'
    if ($sourceFull.StartsWith($targetFull, [StringComparison]::OrdinalIgnoreCase) -or $targetFull.StartsWith($sourceFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The install path '$InstallPath' and the source folder '$SourcePath' must be different folders that do not contain each other."
    }

    # 3. Stop a running service (upgrade), then copy
    Write-Host '[2/8] Stopping the existing service (if any)'
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $serviceExisted = ($null -ne $service)
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    # Steps 3 to 6 replace files and settings while the old service is stopped. If any of them fails on an upgrade,
    # bring the previous service back up so the cafe is not left without a server.
    try {
        Write-Host "[3/8] Copying the program files to $InstallPath"
        New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
        & robocopy.exe $SourcePath $InstallPath /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) {
            throw "robocopy failed with exit code $LASTEXITCODE while copying the server files."
        }

        # 4. Data folder with a tight ACL (cafe.db holds the admin password hash and the agent key)
        Write-Host "[4/8] Preparing the data folder $DataPath"
        New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
        & icacls.exe $DataPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "icacls failed with exit code $LASTEXITCODE while securing $DataPath."
        }
        # A gaming PC that is also this server keeps its emergency-lockout state in the "agent" subfolder. The agent
        # runs as the gaming user, so give Users modify rights on that subfolder only; the database stays restricted.
        $agentStatePath = Join-Path $DataPath 'agent'
        New-Item -ItemType Directory -Force -Path $agentStatePath | Out-Null
        & icacls.exe $agentStatePath /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "icacls failed with exit code $LASTEXITCODE while preparing $agentStatePath."
        }

        # 5. Service
        Write-Host '[5/8] Creating or updating the Windows service'
        $exePath = Join-Path $InstallPath 'Cafe.Server.exe'
        $binaryPath = '"' + $exePath + '"'
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            New-Service -Name $ServiceName -DisplayName $ServiceDisplayName -BinaryPathName $binaryPath -StartupType Automatic | Out-Null
        }
        else {
            # Rewrite the image path directly: it is the exact value "sc.exe config binPath=" would store, without
            # PowerShell 5.1's native-argument quoting pitfalls for paths that contain spaces.
            Set-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $ServiceName) -Name ImagePath -Value $binaryPath
            Set-Service -Name $ServiceName -DisplayName $ServiceDisplayName
        }

        & sc.exe config $ServiceName start= delayed-auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed with exit code $LASTEXITCODE." }
        & sc.exe description $ServiceName 'Cafe Manager server: sessions, billing and reports for the gaming cafe.' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE." }
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE." }
        & sc.exe failureflag $ServiceName 1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed with exit code $LASTEXITCODE." }

        # 6. Firewall (Private and Domain networks only)
        Write-Host "[6/8] Opening firewall port $Port for Private and Domain networks"
        $existingRules = Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue
        if ($null -ne $existingRules) {
            $existingRules | Remove-NetFirewallRule
        }
        New-NetFirewallRule -DisplayName $FirewallRuleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Private, Domain | Out-Null
        try {
            $publicNetworks = @(Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' })
            if ($publicNetworks.Count -gt 0) {
                Write-Warning 'This PC has a network classified as Public. Other devices cannot reach the server on it.'
                Write-Warning 'Switch it to Private: Settings > Network & Internet > (your network) > Network profile type > Private.'
            }
        }
        catch {
            Write-Verbose 'Could not read the network profile.'
        }
    }
    catch {
        $failure = $_.Exception.Message
        if ($serviceExisted) {
            Write-Host 'The upgrade failed. Trying to start the previous server again...' -ForegroundColor Yellow
            try {
                Start-Service -Name $ServiceName -ErrorAction Stop
                Write-Host 'The service was started again. The server is running, but the upgrade was NOT completed.' -ForegroundColor Yellow
            }
            catch {
                Write-Host ('Could not start the service again: ' + $_.Exception.Message) -ForegroundColor Red
                Write-Host 'The server is NOT running. Fix the problem below, then run this installer again.' -ForegroundColor Red
            }
        }
        throw "The install failed before the service was started: $failure"
    }

    # 7. Start and wait
    Write-Host '[7/8] Starting the service'
    Start-Service -Name $ServiceName
    $up = $false
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        if (Test-ServerUp -Url "http://localhost:$Port/") {
            $up = $true
            break
        }
        Start-Sleep -Seconds 2
    }
    if (-not $up) {
        $state = (Get-Service -Name $ServiceName).Status
        Write-Host "The service status is: $state" -ForegroundColor Red
        Write-Host ('Check the log files in ' + (Join-Path $DataPath 'logs')) -ForegroundColor Red
        throw "The server did not answer on http://localhost:$Port/ within 40 seconds."
    }

    # 8. Report
    Write-Host '[8/8] Done. The server is running.' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Open the admin app from another device on the cafe network at:'
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
            $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*'
        })
    foreach ($address in $addresses) {
        Write-Host ('    http://' + $address.IPAddress + ':' + $Port)
    }
    Write-Host ''
    Write-Host "First-run setup (the admin password) must be done on THIS PC at http://localhost:$Port"
    Write-Host "Data, logs and backups are kept in $DataPath"
    if (-not $NoBrowser) {
        Start-Process "http://localhost:$Port"
    }
}

try {
    Install-Server
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
