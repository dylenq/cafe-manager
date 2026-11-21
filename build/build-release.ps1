<#
.SYNOPSIS
    Builds the Cafe Manager release bundle: dist\CafeManager-<version>-win-x64.zip (Windows PowerShell 5.1).

.DESCRIPTION
    Same pipeline as build-release.sh. Environment: SKIP_TESTS=1 skips the test suite;
    CAFE_ALLOW_MISSING_DOCS=1 allows a bundle without docs\INSTALL.md and docs\USER-GUIDE.md.
#>
#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

function Assert-Native {
    param([string]$What)
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root

$propsText = Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw
$match = [regex]::Match($propsText, '<Version>([^<]+)</Version>')
if (-not $match.Success) {
    throw 'No <Version> found in Directory.Build.props.'
}
$Version = $match.Groups[1].Value.Trim()
$Bundle = "CafeManager-$Version-win-x64"
$Stage = Join-Path $Root 'dist\stage'
$BundlePath = Join-Path $Stage $Bundle
# A bundle built without running the tests gets an "-untested" suffix so it cannot be mistaken for a shippable one.
$ZipSuffix = ''
if ($env:SKIP_TESTS -eq '1') { $ZipSuffix = '-untested' }
$Zip = Join-Path $Root "dist\$Bundle$ZipSuffix.zip"
$Front = "CafeManager-$Version-FrontDesk"
$Gaming = "CafeManager-$Version-GamingPC"
$FrontZip = Join-Path $Root "dist\$Front$ZipSuffix.zip"
$GamingZip = Join-Path $Root "dist\$Gaming$ZipSuffix.zip"
Write-Host "== Cafe Manager release $Version =="

Write-Host '[a] Tests'
if ($env:SKIP_TESTS -eq '1') {
    Write-Warning 'SKIP_TESTS=1 - the test suite was NOT run. The zip is named -untested; do not ship it.'
}
else {
    & dotnet test Cafe.sln -c Release --nologo -v minimal
    Assert-Native 'dotnet test'
}

Write-Host "[b] Cleaning $Stage and $Zip"
if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
New-Item -ItemType Directory -Force -Path $BundlePath | Out-Null

Write-Host '[c] Publishing the server (win-x64, self-contained)'
& dotnet publish src/Cafe.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false --nologo -v minimal -o (Join-Path $BundlePath 'Server')
Assert-Native 'dotnet publish Cafe.Server'

Write-Host '[d] Publishing the agent (win-x64, self-contained)'
$AgentMode = 'self-contained'
$AgentOut = Join-Path $BundlePath 'Agent'
& dotnet publish src/Cafe.Agent -c Release -r win-x64 --self-contained true --nologo -v minimal -o $AgentOut
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Self-contained agent publish failed; retrying framework-dependent.'
    if (Test-Path -LiteralPath $AgentOut) { Remove-Item -LiteralPath $AgentOut -Recurse -Force }
    & dotnet publish src/Cafe.Agent -c Release -r win-x64 --self-contained false --nologo -v minimal -o $AgentOut
    Assert-Native 'dotnet publish Cafe.Agent (framework-dependent)'
    $AgentMode = 'framework-dependent'
    $note = 'Agent is framework-dependent: needs the .NET 10 Desktop Runtime on each gaming PC'
    Write-Host "NOTE: $note"
    Set-Content -LiteralPath (Join-Path $AgentOut 'FRAMEWORK-DEPENDENT.txt') -Value $note -Encoding ASCII
}
Write-Host "Agent mode: $AgentMode"

Write-Host '[e] Publishing the agent simulator (win-x64)'
& dotnet publish src/Cafe.AgentSim -c Release -r win-x64 --self-contained false --nologo -v minimal -o (Join-Path $BundlePath 'tools\AgentSim-win-x64')
Assert-Native 'dotnet publish Cafe.AgentSim'

Write-Host '[f] Adding scripts, docs and README'
Copy-Item -Path (Join-Path $Root 'deploy\windows\*.ps1') -Destination $BundlePath
Copy-Item -Path (Join-Path $Root 'deploy\windows\*.cmd') -Destination $BundlePath
$docsPath = Join-Path $BundlePath 'docs'
New-Item -ItemType Directory -Force -Path $docsPath | Out-Null
$haveDocs = (Test-Path -LiteralPath (Join-Path $Root 'docs\INSTALL.md')) -and (Test-Path -LiteralPath (Join-Path $Root 'docs\USER-GUIDE.md'))
if (-not $haveDocs) {
    if ($env:CAFE_ALLOW_MISSING_DOCS -eq '1') {
        Write-Warning 'docs\INSTALL.md or docs\USER-GUIDE.md is missing (allowed by CAFE_ALLOW_MISSING_DOCS=1).'
    }
    else {
        throw 'docs\INSTALL.md and docs\USER-GUIDE.md are required (set CAFE_ALLOW_MISSING_DOCS=1 to skip).'
    }
}
if (Test-Path -LiteralPath (Join-Path $Root 'docs')) {
    Copy-Item -Path (Join-Path $Root 'docs\*.md') -Destination $docsPath
}
Copy-Item -LiteralPath (Join-Path $Root 'build\bundle-README.txt') -Destination (Join-Path $BundlePath 'README.txt')

Write-Host '[f2] Staging the client packages (front desk, gaming PC)'
$FrontPath = Join-Path $Stage $Front
$GamingPath = Join-Path $Stage $Gaming
New-Item -ItemType Directory -Force -Path $FrontPath, $GamingPath | Out-Null
Copy-Item -LiteralPath (Join-Path $BundlePath 'Server'), (Join-Path $BundlePath 'docs') -Destination $FrontPath -Recurse
foreach ($name in @('install-server.ps1', 'uninstall-server.ps1', 'Install Cafe Manager Server.cmd', 'Uninstall Cafe Manager Server.cmd')) {
    Copy-Item -LiteralPath (Join-Path $BundlePath $name) -Destination $FrontPath
}
Copy-Item -LiteralPath (Join-Path $Root 'build\client\README-FrontDesk.txt') -Destination (Join-Path $FrontPath 'README.txt')
Copy-Item -LiteralPath (Join-Path $BundlePath 'Agent') -Destination $GamingPath -Recurse
foreach ($name in @('install-agent.ps1', 'uninstall-agent.ps1', 'Install on this Gaming PC.cmd', 'Uninstall from this Gaming PC.cmd')) {
    Copy-Item -LiteralPath (Join-Path $BundlePath $name) -Destination $GamingPath
}
Copy-Item -LiteralPath (Join-Path $Root 'build\client\README-GamingPC.txt') -Destination (Join-Path $GamingPath 'README.txt')

Write-Host '[g] Safety scan'
$dbFiles = @(Get-ChildItem -LiteralPath $Stage -Recurse -File | Where-Object { $_.Name -match '\.(db(-wal|-shm)?|sqlite3?|tmp)$' })
if ($dbFiles.Count -gt 0) {
    throw ('Database or temporary files in the bundle: ' + (($dbFiles | ForEach-Object { $_.FullName }) -join ', '))
}
foreach ($settingsFile in @(Get-ChildItem -LiteralPath $Stage -Recurse -File -Filter 'agentsettings.json')) {
    if ((Get-Content -LiteralPath $settingsFile.FullName -Raw) -match '"SharedKey"\s*:\s*"[^"]+"') {
        throw "$($settingsFile.FullName) contains a non-empty SharedKey."
    }
}
foreach ($logDir in @(Get-ChildItem -LiteralPath $Stage -Recurse -Directory -Filter 'logs')) {
    if (@(Get-ChildItem -LiteralPath $logDir.FullName -Recurse -File).Count -gt 0) {
        throw "Log files in the bundle: $($logDir.FullName)"
    }
}

Write-Host "[h] Zipping $Zip, $FrontZip and $GamingZip"
# Build each zip entry by entry: Compress-Archive in Windows PowerShell 5.1 stores backslash separators and fails on
# files held open, which gives a different layout than build-release.sh. Entry names are written explicitly with
# forward slashes and start with the package folder name, exactly like "zip -r" in the .sh script.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function New-PackageZip {
    param([string]$FolderPath, [string]$ZipPath)
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $zipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $prefixLength = $Stage.TrimEnd('\').Length + 1
            foreach ($file in @(Get-ChildItem -LiteralPath $FolderPath -Recurse -File | Sort-Object FullName)) {
                $entryName = $file.FullName.Substring($prefixLength).Replace('\', '/')
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $zipStream.Dispose()
    }
}
New-PackageZip -FolderPath $BundlePath -ZipPath $Zip
New-PackageZip -FolderPath $FrontPath -ZipPath $FrontZip
New-PackageZip -FolderPath $GamingPath -ZipPath $GamingZip

Write-Host '[i] Result'
$item = Get-Item -LiteralPath $Zip
Write-Host "Zip:     $($item.FullName)"
Write-Host ("Size:    {0:N1} MB" -f ($item.Length / 1MB))
Write-Host "Agent:   $AgentMode"
foreach ($z in @($Zip, $FrontZip, $GamingZip)) {
    $zi = Get-Item -LiteralPath $z
    Write-Host ("{0,8:N1} MB  {1}  {2}" -f ($zi.Length / 1MB), (Get-FileHash -LiteralPath $z -Algorithm SHA256).Hash.ToLowerInvariant(), $zi.Name)
}
