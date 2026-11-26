<#
.SYNOPSIS
    Installs or upgrades the Cafe Manager agent on a gaming PC.

.DESCRIPTION
    Run this on each gaming PC from the extracted release bundle:

      powershell -NoProfile -ExecutionPolicy Bypass -File .\install-agent.ps1 -ServerUrl http://192.168.1.10:5080 -SharedKey <key> -EmergencyCode <code>

    The shared key is shown on the server's Settings page. The emergency code (at least 6 characters) is what staff
    type on the lock screen if the server is unreachable; only its PBKDF2 hash is stored on the PC. Parameter values
    are visible in process listings only while this installer runs (accepted risk).

    The script:
      1. asks for administrator rights (a UAC prompt appears) if it does not have them,
      2. resolves the gaming user account,
      3. copies the agent to C:\Program Files\CafeManager\Agent (stopping a running agent first),
      4. hashes the emergency code with Cafe.Agent.exe --hash-code,
      5. writes agentsettings.json (read-only for the gaming user) and a writable logs folder,
      6. registers the "CafeManager Agent" scheduled task: at logon of the gaming user, highest privileges,
         restart every minute (up to 999 times), no time limit, runs on battery,
      7. disables Task Manager for the gaming user (DisableTaskMgr=1), also when that user is not logged in,
      8. starts the agent now if the gaming user is logged on.

    It is safe to run again: a re-run replaces the files, the settings and the task. No firewall rule is added
    (outbound connections are allowed by default).

.PARAMETER ServerUrl
    Address of the server, for example http://192.168.1.10:5080. A bare IP address or name is accepted and becomes
    http://<address>:5080. When ServerUrl, SharedKey or EmergencyCode is left out, the script asks for it, so the
    "Install on this Gaming PC.cmd" launcher can be double-clicked without any arguments.

.PARAMETER SharedKey
    The agent shared key from the server's Settings page.

.PARAMETER EmergencyCode
    Emergency unlock code, at least 6 characters.

.PARAMETER MachineId
    Optional machine name the server shows for this PC. Default: empty (the agent uses the computer name).

.PARAMETER GamingUser
    The Windows account that plays on this PC, as user or DOMAIN\user. Default: the user running this script.

.PARAMETER SourcePath
    Folder holding the published agent (contains Cafe.Agent.exe). Default: the "Agent" folder next to this script.

.PARAMETER InstallPath
    Where the agent is installed. Default: C:\Program Files\CafeManager\Agent.

.PARAMETER NoPause
    Do not wait for Enter at the end (for unattended runs).
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ServerUrl = '',
    [string]$SharedKey = '',
    [string]$EmergencyCode = '',

    [string]$MachineId = '',
    [string]$GamingUser = '',
    [string]$SourcePath = '',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'CafeManager\Agent'),
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
if ([string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath = Join-Path $ScriptDir 'Agent' }

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$TaskName = 'CafeManager Agent'
$ProcessName = 'Cafe.Agent'
$TempHiveName = 'CafeManagerTemp'
$PolicyKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'

# The gaming user is the user who started this script. Capture it BEFORE elevation (the elevated process may run
# as a different administrator account) and pass it through to the elevated run.
if ([string]::IsNullOrWhiteSpace($GamingUser)) {
    $GamingUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $ScriptArguments['GamingUser'] = $GamingUser
}

# Files extracted from a zip downloaded from the internet carry a "blocked" mark that makes Windows refuse or warn
# about them; clear it on the release folder so the agent program itself can run.
Get-ChildItem -LiteralPath $ScriptDir -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

# Turns "192.168.1.10" or "frontdesk" into "http://192.168.1.10:5080"; a full http(s) address is kept as typed.
function ConvertTo-ServerUrl {
    param([string]$Value)
    $v = $Value.Trim().TrimEnd('/')
    if ($v -eq '') { return '' }
    if ($v -notmatch '^[A-Za-z][A-Za-z0-9+.-]*://') {
        if ($v -notmatch ':\d+$') { $v = $v + ':5080' }
        $v = 'http://' + $v
    }
    return $v
}

# Ask for anything that was not passed on the command line (double-click install). This runs before elevation and
# the answers are handed to the elevated run as parameters.
$NeedsPrompt = [string]::IsNullOrWhiteSpace($ServerUrl) -or [string]::IsNullOrWhiteSpace($SharedKey) -or [string]::IsNullOrWhiteSpace($EmergencyCode)
if ($NoPause -and ($NeedsPrompt -or $EmergencyCode.Length -lt 6)) {
    # Unattended run: never wait for typed answers.
    Write-Host 'ERROR: -ServerUrl, -SharedKey and -EmergencyCode (at least 6 characters) are required with -NoPause.' -ForegroundColor Red
    exit 1
}
if ($NeedsPrompt) {
    Write-Host ''
    Write-Host '== Cafe Manager - set up this gaming PC ==' -ForegroundColor Cyan
    Write-Host 'You need two things from the front-desk app (Settings page): the server address and the shared key.'
    Write-Host ''
}
if (-not $ScriptArguments.ContainsKey('GamingUser') -or $NeedsPrompt) {
    if ($NeedsPrompt) {
        $answer = Read-Host "Windows account the players use on this PC [press Enter for $GamingUser]"
        if (-not [string]::IsNullOrWhiteSpace($answer)) { $GamingUser = $answer.Trim() }
    }
    $ScriptArguments['GamingUser'] = $GamingUser
}
while ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    $ServerUrl = ConvertTo-ServerUrl (Read-Host 'Server address (for example 192.168.1.10)')
}
$ServerUrl = ConvertTo-ServerUrl $ServerUrl
$ScriptArguments['ServerUrl'] = $ServerUrl
while ([string]::IsNullOrWhiteSpace($SharedKey)) {
    $SharedKey = (Read-Host 'Shared key (front-desk app: Settings > PC agents)').Trim()
    $ScriptArguments['SharedKey'] = $SharedKey
}
while ($EmergencyCode.Length -lt 6) {
    $first = Read-Host 'Choose an emergency unlock code (at least 6 characters, keep it secret)'
    if ($first.Length -lt 6) { Write-Host 'The code must be at least 6 characters.' -ForegroundColor Yellow; continue }
    $second = Read-Host 'Type the emergency code again'
    if ($first -cne $second) { Write-Host 'The two codes did not match. Try again.' -ForegroundColor Yellow; continue }
    $EmergencyCode = $first
    $ScriptArguments['EmergencyCode'] = $EmergencyCode
}
if ($EmergencyCode.Length -gt 200) {
    Write-Host 'ERROR: the emergency code must be at most 200 characters.' -ForegroundColor Red
    exit 1
}

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
# (for example C:\Program Files\CafeManager\Agent), and must not be a drive root or a Windows system folder.
function Assert-SafeInstallPath {
    param([string]$Path)
    $example = 'C:\Program Files\CafeManager\Agent'
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

# True when the account is a direct member of the local Administrators group (S-1-5-32-544). Nested group
# membership is not expanded; when the group cannot be read the answer is $false.
function Test-IsLocalAdministrator {
    param([string]$Sid)
    try {
        foreach ($member in @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop)) {
            if ($member.SID.Value -eq $Sid) { return $true }
        }
    }
    catch {
        Write-Verbose 'Could not read the local Administrators group.'
    }
    return $false
}

function Resolve-UserSid {
    param([string]$Account)
    try {
        $nt = New-Object System.Security.Principal.NTAccount($Account)
        return $nt.Translate([System.Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "The Windows account '$Account' does not exist on this PC. Pass -GamingUser with an existing account (user or DOMAIN\user)."
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
    # NTUSER.DAT temporarily loaded. Returns $false when the user has no profile yet.
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

function Set-TaskManagerPolicy {
    param([string]$Sid)
    $applied = Invoke-WithUserHive -Sid $Sid -Action {
        param($Root)
        $key = Join-Path $Root $PolicyKeyPath
        if (-not (Test-Path -LiteralPath $key)) {
            New-Item -Path $key -Force | Out-Null
        }
        New-ItemProperty -LiteralPath $key -Name 'DisableTaskMgr' -Value 1 -PropertyType DWord -Force | Out-Null
    }
    return $applied
}

function Stop-RunningAgent {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    }
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) { return }
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
    if (@(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'The running agent did not stop within 20 seconds. Close it and run the installer again.'
    }
}

function Test-UserLoggedOn {
    param([string]$Account)
    try {
        $current = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName
        if (-not [string]::IsNullOrWhiteSpace($current) -and ($current -ieq $Account)) { return $true }
        $short = $Account.Split('\')[-1]
        if (-not [string]::IsNullOrWhiteSpace($current) -and ($current.Split('\')[-1] -ieq $short)) { return $true }
    }
    catch {
        Write-Verbose 'Could not read the logged-on user.'
    }
    return $false
}

function Install-Agent {
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

    Write-Host '== Cafe Manager agent install ==' -ForegroundColor Cyan

    # Parameter checks
    $uri = $null
    if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$uri) -or ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
        throw "ServerUrl '$ServerUrl' is not a valid http or https address. Example: http://192.168.1.10:5080"
    }
    if ([string]::IsNullOrWhiteSpace($SharedKey)) {
        throw 'SharedKey must not be empty. Copy it from the server Settings page.'
    }

    Write-Host "[1/8] Resolving the gaming user '$GamingUser'"
    $sid = Resolve-UserSid -Account $GamingUser
    $account = $sid.Translate([System.Security.Principal.NTAccount]).Value
    Write-Host "      $account ($($sid.Value))"

    Write-Host "[2/8] Checking the agent files in $SourcePath"
    if (-not (Test-Path -LiteralPath (Join-Path $SourcePath 'Cafe.Agent.exe'))) {
        throw "Cafe.Agent.exe was not found in '$SourcePath'. Run this script from the extracted release folder, or pass -SourcePath."
    }

    # Mirroring a folder into itself (or into one of its parents) would delete the source files.
    $sourceFull = ([IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SourcePath).ProviderPath)).TrimEnd('\') + '\'
    $targetFull = ([IO.Path]::GetFullPath($InstallPath)).TrimEnd('\') + '\'
    if ($sourceFull.StartsWith($targetFull, [StringComparison]::OrdinalIgnoreCase) -or $targetFull.StartsWith($sourceFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The install path '$InstallPath' and the source folder '$SourcePath' must be different folders that do not contain each other."
    }
    $frameworkNote = Join-Path $SourcePath 'FRAMEWORK-DEPENDENT.txt'
    if (Test-Path -LiteralPath $frameworkNote) {
        Write-Warning 'This agent build is framework-dependent: the .NET 10 Desktop Runtime must be installed on this PC.'
    }

    Write-Host "[3/8] Copying the agent to $InstallPath"
    Stop-RunningAgent
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    # Keep the settings and logs of an earlier install out of the mirror deletion.
    & robocopy.exe $SourcePath $InstallPath /MIR /R:3 /W:2 /XF agentsettings.json /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE while copying the agent files."
    }
    $exePath = Join-Path $InstallPath 'Cafe.Agent.exe'

    Write-Host '[4/8] Hashing the emergency code'
    $tempOut = Join-Path ([IO.Path]::GetTempPath()) ('cafe-hash-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $hash = $null
    try {
        $proc = Start-Process -FilePath $exePath -ArgumentList @('--hash-code', (Quote-NativeArg $EmergencyCode)) `
            -Wait -PassThru -NoNewWindow -RedirectStandardOutput $tempOut
        # The agent is a WinExe: give the redirected output a moment to land on disk.
        Start-Sleep -Milliseconds 300
        if ($proc.ExitCode -ne 0) {
            throw "Cafe.Agent.exe --hash-code failed with exit code $($proc.ExitCode). The emergency code must be at least 6 characters."
        }
        if (Test-Path -LiteralPath $tempOut) {
            foreach ($line in (Get-Content -LiteralPath $tempOut)) {
                if (-not [string]::IsNullOrWhiteSpace($line)) {
                    $hash = $line.Trim()
                    break
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempOut) {
            Remove-Item -LiteralPath $tempOut -Force -ErrorAction SilentlyContinue
        }
    }
    if ($null -eq $hash -or $hash -notmatch '^pbkdf2-sha256\$\d+\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$') {
        throw 'The agent did not return a valid emergency code hash. Check that Cafe.Agent.exe runs on this PC (the .NET runtime may be missing).'
    }

    Write-Host '[5/8] Writing agentsettings.json and securing it'
    $settings = [ordered]@{
        ServerUrl               = $ServerUrl
        SharedKey               = $SharedKey
        MachineId               = $MachineId
        EmergencyUnlockCodeHash = $hash
    }
    $json = $settings | ConvertTo-Json
    $settingsPath = Join-Path $InstallPath 'agentsettings.json'
    # Create an empty temp file, lock its ACL down, and only then write the secrets into it, so the SharedKey and the
    # emergency hash are never readable through the inherited Program Files ACL. The finished file replaces the old
    # one by rename (a rename keeps the temp file's own ACL, so a previous gaming user's entry cannot linger).
    $tempSettingsPath = Join-Path $InstallPath ('agentsettings.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [System.IO.File]::WriteAllBytes($tempSettingsPath, (New-Object byte[] 0))
        & icacls.exe $tempSettingsPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' ("*" + $sid.Value + ":R") | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "icacls failed with exit code $LASTEXITCODE while securing agentsettings.json."
        }
        [System.IO.File]::WriteAllText($tempSettingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $settingsPath) {
            Remove-Item -LiteralPath $settingsPath -Force
        }
        Move-Item -LiteralPath $tempSettingsPath -Destination $settingsPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $tempSettingsPath) {
            Remove-Item -LiteralPath $tempSettingsPath -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-IsLocalAdministrator -Sid $sid.Value) {
        Write-Warning "$account is a member of the local Administrators group. An administrator can edit agentsettings.json, stop the agent and re-enable Task Manager, so the lock is only a speed bump for that account."
        Write-Warning 'For real protection run the gaming PC as a standard (non-administrator) account and pass it with -GamingUser.'
    }
    $logsPath = Join-Path $InstallPath 'logs'
    New-Item -ItemType Directory -Force -Path $logsPath | Out-Null
    & icacls.exe $logsPath /grant ("*" + $sid.Value + ":(OI)(CI)M") | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls failed with exit code $LASTEXITCODE while preparing the logs folder."
    }

    Write-Host "[6/8] Registering the scheduled task '$TaskName'"
    $action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $InstallPath
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
    $principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew -StartWhenAvailable
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $taskSettings -Description 'Cafe Manager agent: locks this PC and talks to the server.' -Force | Out-Null

    Write-Host '[7/8] Disabling Task Manager for the gaming user'
    $policyApplied = Set-TaskManagerPolicy -Sid $sid.Value
    if (-not $policyApplied) {
        Write-Warning "$account has no profile on this PC yet (never logged in). Task Manager was NOT disabled."
        Write-Warning 'After that user has logged in once, run this installer again to apply the policy.'
    }

    Write-Host '[8/8] Starting the agent'
    if (Test-UserLoggedOn -Account $account) {
        Start-ScheduledTask -TaskName $TaskName
        Write-Host '      Started now for the logged-on user.'
    }
    else {
        Write-Host "      $account is not logged on. The agent starts at that user's next logon."
    }

    Write-Host ''
    Write-Host 'Done.' -ForegroundColor Green
    Write-Host "  Install path : $InstallPath"
    Write-Host "  Task         : $TaskName"
    Write-Host "  Gaming user  : $account"
    Write-Host "  Server URL   : $ServerUrl"
}

try {
    Install-Agent
    Wait-ForEnter
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    Wait-ForEnter
    exit 1
}
