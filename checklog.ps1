param([int]$Last = 60, [string]$Filter = "", [string]$Category = "provider")
# Logs live under %LOCALAPPDATA%\ClaudeWidget\Logs\{category}.log per
# Logger.cs's Environment.SpecialFolder.LocalApplicationData call - but
# since the widget COM-server process runs as a packaged (MSIX) app, Windows
# transparently redirects that special folder to a package-private path:
#   %LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalCache\Local\ClaudeWidget\Logs\
# Resolve the real path via Get-AppxPackage rather than hardcoding the
# family name, since a cert/publisher change would change it.
$pkg = Get-AppxPackage -Name "ClaudeWidget" -ErrorAction SilentlyContinue
if (-not $pkg) {
    Write-Host "ClaudeWidget is not installed (Get-AppxPackage returned nothing)."
    exit 1
}
$logPath = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\ClaudeWidget\Logs\$Category.log"
if (-not (Test-Path $logPath)) {
    Write-Host "No log file at $logPath yet."
    exit 1
}
$lines = Get-Content $logPath -Encoding UTF8
if ($Filter) { $lines = $lines | Select-String $Filter | ForEach-Object { $_.Line } }
$lines | Select-Object -Last $Last
