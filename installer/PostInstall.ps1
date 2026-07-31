# PostInstall.ps1 — run by the Inno Setup installer (already elevated) after
# copying files. Trusts the bundled certificate, then installs the MSIX.
#
# NOTE: the app is built with <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
# (see ClaudeWidget.csproj), so the Windows App Runtime is baked directly
# into the MSIX — there is no separate Windows App SDK Runtime dependency to
# install here. (An earlier version of this installer bundled and prompted
# to run a ~110MB WindowsAppRuntimeInstall-x64.exe; that's no longer needed.)

param(
    [Parameter(Mandatory = $true)][string]$MsixPath,
    [Parameter(Mandatory = $true)][string]$CerPath
)

$ErrorActionPreference = "Stop"
$LogFile = Join-Path $env:TEMP "ClaudeWidget_install.log"
Start-Transcript -Path $LogFile -Append | Out-Null

try {
    # On some machines, Windows PowerShell 5.1's module autoloader picks up a
    # PowerShell 7 (Store app) copy of Microsoft.PowerShell.Security instead
    # of its own native one, and that copy fails to import — which leaves the
    # Cert:\ PSDrive unavailable ("DriveNotFound") for Import-Certificate.
    # Force-load the native copy to guarantee Cert:\ works regardless.
    Remove-Module Microsoft.PowerShell.Security -ErrorAction SilentlyContinue
    Import-Module "$PSHome\Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1" -Force -ErrorAction SilentlyContinue

    Write-Host "[1/3] Importing certificate..."
    Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

    Write-Host "[2/3] Stopping any running instance..."
    Get-Process | Where-Object { $_.Name -like "*ClaudeWidget*" } | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }

    Write-Host "[3/3] Installing package..."
    Add-AppxPackage -Path $MsixPath -ForceApplicationShutdown

    $pkg = Get-AppxPackage "ClaudeWidget" -ErrorAction SilentlyContinue
    if (-not $pkg) { throw "Add-AppxPackage did not report a successfully installed package." }
    Write-Host "Installed: $($pkg.PackageFullName)"

    Get-Process -Name "Widgets" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name "WidgetService" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host "Done."
}
catch {
    Write-Host "ERROR: $_"
    Stop-Transcript | Out-Null
    Write-Host "See $LogFile for details."
    exit 1
}

Stop-Transcript | Out-Null
