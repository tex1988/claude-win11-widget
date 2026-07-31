# PreUninstall.ps1 — run by the Inno Setup uninstaller (already elevated)
# before it deletes any files. Removes the AppX package and the certificate
# that PostInstall.ps1 trusted for it.
#
# Reads ClaudeUsageWidgetLocal.cer from the same folder (still present at
# this point — Inno Setup runs [UninstallRun] before removing [Files]) to
# compute the exact thumbprint to remove, so this doesn't depend on any
# separate state file surviving between install and uninstall.

$ErrorActionPreference = "Stop"
$LogFile = Join-Path $env:TEMP "ClaudeWidget_uninstall.log"
Start-Transcript -Path $LogFile -Append | Out-Null

try {
    Remove-Module Microsoft.PowerShell.Security -ErrorAction SilentlyContinue
    Import-Module "$PSHome\Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1" -Force -ErrorAction SilentlyContinue

    Write-Host "[1/3] Stopping any running instance..."
    Get-Process | Where-Object { $_.Name -like "*ClaudeWidget*" } | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }

    Write-Host "[2/3] Removing package..."
    $pkg = Get-AppxPackage "ClaudeWidget" -ErrorAction SilentlyContinue
    if ($pkg) {
        $pkg | Remove-AppxPackage -ErrorAction SilentlyContinue
        Write-Host "Removed: $($pkg.PackageFullName)"
    } else {
        Write-Host "No installed ClaudeWidget package found."
    }

    Write-Host "[3/3] Removing trusted certificate..."
    $cerPath = Join-Path $PSScriptRoot "ClaudeUsageWidgetLocal.cer"
    if (Test-Path $cerPath) {
        $certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
        $thumbprint = $certObj.Thumbprint
        foreach ($storePath in @("Cert:\LocalMachine\Root", "Cert:\LocalMachine\TrustedPeople")) {
            $cert = Get-ChildItem $storePath -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $thumbprint }
            if ($cert) {
                $cert | Remove-Item -ErrorAction SilentlyContinue
                Write-Host "Removed certificate $thumbprint from $storePath"
            }
        }
    } else {
        Write-Host "Certificate file not found at $cerPath; skipping cert cleanup."
    }

    Get-Process -Name "Widgets" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name "WidgetService" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host "Done."
}
catch {
    Write-Host "ERROR: $_"
    Stop-Transcript | Out-Null
    exit 1
}

Stop-Transcript | Out-Null
