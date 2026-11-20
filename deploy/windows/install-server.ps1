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
    [string]$SourcePath = (Join-Path $PSScriptRoot 'Server'),
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'CafeManager\Server'),
    [switch]$NoBrowser,
    [switch]$NoPause
)

# Captured at script scope: inside a function $PSBoundParameters would be the function's own (empty) set.
$ScriptArguments = $PSBoundParameters

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$ServiceName = 'CafeManager'
$ServiceDisplayName = 'Cafe Manager Server'
$FirewallRuleName = 'Cafe Manager (TCP 5080)'
$Port = 5080
$DataPath = Join-Path $env:ProgramData 'CafeManager'

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
    # 1. Elevation
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

    Write-Host '== Cafe Manager server install ==' -ForegroundColor Cyan

    # 2. Validate the source
    Write-Host "[1/8] Checking the server files in $SourcePath"
    if (-not (Test-Path -LiteralPath (Join-Path $SourcePath 'Cafe.Server.exe'))) {
        throw "Cafe.Server.exe was not found in '$SourcePath'. Run this script from the extracted release folder, or pass -SourcePath."
    }

    # 3. Stop a running service (upgrade), then copy
    Write-Host '[2/8] Stopping the existing service (if any)'
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    Write-Host "[3/8] Copying the program files to $InstallPath"
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    & robocopy.exe $SourcePath $InstallPath /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
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
    Wait-ForEnter
    exit 1
}
