<#
.SYNOPSIS
    Pull the most recent screenshots / recordings off the XR headset.

.DESCRIPTION
    Capture folders differ between headsets and OS versions, so this does not
    hardcode one. It searches the usual media roots, sorts everything by
    modification time, and pulls the newest N files.

    Requires adb on PATH (ships with Android platform-tools; Unity also
    bundles a copy under its Android SDK).

.EXAMPLE
    .\pull-headset-captures.ps1
    Pull the single newest capture into .\captures

.EXAMPLE
    .\pull-headset-captures.ps1 -Count 5 -Dest docs\images
    Pull the five newest captures into docs\images
#>

param(
    [int]$Count = 1,
    [string]$Dest = "captures"
)

$ErrorActionPreference = "Stop"

# Media roots to search. Harmless if some do not exist.
$roots = @(
    "/sdcard/DCIM",
    "/sdcard/Pictures",
    "/sdcard/Movies"
)

Write-Host "Checking for a connected device..."
$devices = (& adb devices) | Select-Object -Skip 1 | Where-Object { $_ -match "\sdevice$" }
if (-not $devices) {
    Write-Error "No device found. Put the headset on, approve the USB debugging prompt, then retry. For wireless: adb connect <headset-ip>:5555"
}

# -t sorts newest first. Redirect stderr per-root so a missing folder does not
# abort the whole listing.
$findCmd = ($roots | ForEach-Object { "find $_ -type f 2>/dev/null" }) -join "; "
$listCmd = "($findCmd) | xargs -r ls -1t 2>/dev/null | head -n $Count"

$remote = & adb shell $listCmd
$remote = $remote | Where-Object { $_.Trim() -ne "" } | ForEach-Object { $_.Trim() }

if (-not $remote) {
    Write-Error "No capture files found under: $($roots -join ', '). Take a screenshot in the headset first."
}

if (-not (Test-Path $Dest)) { New-Item -ItemType Directory -Path $Dest | Out-Null }

foreach ($file in $remote) {
    $name = Split-Path $file -Leaf
    $target = Join-Path $Dest $name
    Write-Host "Pulling $name"
    & adb pull "$file" "$target" | Out-Null
    Write-Host "  -> $target"
}

Write-Host ""
Write-Host "Done. $($remote.Count) file(s) in $Dest"
