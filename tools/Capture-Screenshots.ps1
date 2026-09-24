<#
.SYNOPSIS
    Regenerates the README screenshots from the demo snapshots in samples/.

.DESCRIPTION
    Builds the app, starts it once per demo, drives it with UI Automation (clicking tabs and buttons by their
    accessible names), and saves the window to docs/screenshots. Run it from the repository root on Windows:

        powershell -ExecutionPolicy Bypass -File tools/Capture-Screenshots.ps1

    Pass -Only build,champselect to capture a subset, or -OutDir to write somewhere else. Screens that use op.gg
    (champ select, the matchup line) need an internet connection. Don't touch the mouse while it runs: every
    capture brings the window to the front.
#>
param(
    [string]$OutDir = "docs/screenshots",
    [string[]]$Only = @(),
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $NoBuild) {
    dotnet build src/LeagueClanker.App -c Debug --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
$exe = Get-ChildItem "src/LeagueClanker.App/bin/Debug" -Recurse -Filter "LeagueClanker.App.exe" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "LeagueClanker.App.exe not found; build the app first" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CaptureWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[CaptureWin32]::SetProcessDPIAware() | Out-Null
$AE = [System.Windows.Automation.AutomationElement]

function Start-App([string[]]$Arguments) { Start-Process $exe.FullName -ArgumentList $Arguments -PassThru }

function Get-Window($p) {
    for ($i = 0; $i -lt 60; $i++) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }; Start-Sleep -Milliseconds 250 }
    throw "The app window didn't appear"
}

function Find-Element($p, [string]$Name, [switch]$Desktop) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $Name
    for ($i = 0; $i -lt 40; $i++) {
        $scope = if ($Desktop) { $AE::RootElement } else { $AE::FromHandle((Get-Window $p)) }
        $element = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 250
    }
    throw "Element '$Name' not found"
}

function Find-ById($p, [string]$Id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), $Id
    $AE::FromHandle((Get-Window $p)).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element($p, [string]$Name) {
    (Find-Element $p $Name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
}

function Select-Tab($p, [string]$Name) {
    (Find-Element $p $Name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}

function Set-Search($p, [string]$Text) {
    (Find-ById $p "AugmentSearch").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Start-Sleep -Milliseconds 300
}

function Save-Window($p, [string]$Name) {
    $handle = Get-Window $p
    [CaptureWin32]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 500
    $rect = New-Object CaptureWin32+RECT
    [CaptureWin32]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap ($rect.R - $rect.L), ($rect.B - $rect.T)
    [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($rect.L, $rect.T, 0, 0, $bitmap.Size)
    $path = Join-Path $OutDir $Name
    $bitmap.Save($path)
    Write-Host "$Name  $($bitmap.Width)x$($bitmap.Height)"
}

function Wanted([string]$Name) { $Only.Count -eq 0 -or $Only -contains $Name }

$shots = [ordered]@{
    build = {
        $p = Start-App "--demo", "samples/ap-heavy.json"
        Start-Sleep -Seconds 6
        Save-Window $p "build.png"
        $p
    }
    compact = {
        $p = Start-App "--demo", "samples/ap-heavy.json"
        Start-Sleep -Seconds 6
        Invoke-Element $p "Compact mode"
        Start-Sleep -Milliseconds 800
        Save-Window $p "compact.png"
        Invoke-Element $p "Compact mode"   # back to the full view, so the next start isn't compact
        $p
    }
    pivot = {
        $p = Start-App "--demo", "samples/pivot-demo"
        Start-Sleep -Seconds 14
        Save-Window $p "pivot-suggested.png"
        Invoke-Element $p "Keep mine"
        Start-Sleep -Milliseconds 800
        Save-Window $p "pivot-declined.png"
        Start-Sleep -Seconds 8
        Select-Tab $p "Players"
        Save-Window $p "players.png"
        $p
    }
    augments = {
        $p = Start-App "--demo", "samples/mayhem/jinx-level7.json"
        Start-Sleep -Seconds 5
        Select-Tab $p "Augments"
        Set-Search $p "its crit";     Invoke-Element $p "Have It's Critical"
        Set-Search $p "critical rhy"; Invoke-Element $p "Offer Critical Rhythm"
        Set-Search $p "recursion";    Invoke-Element $p "Offer Recursion"
        Set-Search $p "celestial";    Invoke-Element $p "Offer Celestial Body"
        Start-Sleep -Seconds 3
        Save-Window $p "augments.png"
        $p
    }
    classic = {
        $p = Start-App "--demo", "samples/classic/ashe-vs-tanks.json"
        Start-Sleep -Seconds 6
        Save-Window $p "classic.png"
        $p
    }
    champselect = {
        $p = Start-App "--champselect", "samples/champselect/top-vs-darius.json"
        Start-Sleep -Seconds 8
        Save-Window $p "champselect.png"
        $p
    }
    draft = {
        $p = Start-App "--champselect", "samples/champselect/ban-phase.json"
        Start-Sleep -Seconds 8
        Save-Window $p "draft.png"
        $p
    }
    settings = {
        $p = Start-App "--champselect", "samples/champselect/leona-support.json"
        Start-Sleep -Seconds 4
        Invoke-Element $p "Settings"
        Start-Sleep -Milliseconds 600
        Save-Window $p "settings.png"
        $p
    }
}

foreach ($name in $shots.Keys) {
    if (-not (Wanted $name)) { continue }
    $p = $null
    try { $p = & $shots[$name] }
    finally { if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}
