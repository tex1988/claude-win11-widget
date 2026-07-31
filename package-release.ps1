# package-release.ps1 — builds a Release MSIX, signs it with the local
# ClaudeUsageWidgetLocal cert, and wraps everything into a single
# ClaudeWidgetSetup.exe installer (via Inno Setup) that:
#   - trusts the bundled certificate
#   - installs the MSIX
#   - registers a normal Add/Remove Programs entry whose Uninstall removes
#     both the app and the certificate
#
# NOTE: the app is built with <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
# (see ClaudeWidget.csproj), so the Windows App Runtime is baked directly
# into the MSIX -- no separate Windows App SDK Runtime install step is
# needed on target machines, and no runtime installer needs to be bundled
# here. (An earlier version of this script bundled a ~110MB
# WindowsAppRuntimeInstall-x64.exe and prompted the user to run it; that
# approach was dropped once self-contained deployment was adopted.)
#
# Run from widget2/. Reuses the same signing cert as deploy.ps1 (see that
# script's comments for how to find the correct machine-trusted thumbprint
# if this one ever needs regenerating).
#
# Requires Inno Setup 6 (ISCC.exe) — install via:
#   winget install --id JRSoftware.InnoSetup -e

$ErrorActionPreference = "Stop"

# On this machine, Windows PowerShell 5.1's module autoloader can pick up a
# PowerShell 7 (Store app) copy of Microsoft.PowerShell.Security instead of
# its own native one (whichever comes first on $env:PSModulePath), and that
# copy fails to import (duplicate TypeData registration errors). When that
# happens the Cert:\ PSDrive provider never gets registered, so any
# Cert:\... path below throws "DriveNotFound". Force-load the native copy
# from $PSHome to guarantee Cert:\ is available regardless of module path
# ordering.
Remove-Module Microsoft.PowerShell.Security -ErrorAction SilentlyContinue
Import-Module "$PSHome\Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1" -Force

$RepoRoot       = $PSScriptRoot
$StagingDir     = Join-Path $RepoRoot "release\staging"
$OutputDir      = Join-Path $RepoRoot "release\output"

# local.settings.ps1 (gitignored) supplies $CertThumbprint — see
# local.settings.ps1.example for how to create it.
$localSettings = Join-Path $RepoRoot "local.settings.ps1"
if (-not (Test-Path $localSettings)) {
    throw "local.settings.ps1 not found. Copy local.settings.ps1.example to local.settings.ps1 and fill in your cert thumbprint."
}
. $localSettings
$certThumbprint = $CertThumbprint

# --- Locate ISCC.exe (Inno Setup compiler) --------------------------------
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found. Install Inno Setup 6: winget install --id JRSoftware.InnoSetup -e" }

Write-Host "=== Building Release MSIX ===" -ForegroundColor Cyan
dotnet build "$RepoRoot\ClaudeWidget\ClaudeWidget.csproj" `
    -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
    -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false `
    -p:UapAppxPackageBuildMode=SideloadOnly
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$msixDir = "D:\BuildCache\ClaudeWidget\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64"
$msix = Get-ChildItem -Path $msixDir -Filter "*.msix" | Select-Object -First 1
if (-not $msix) { throw "No .msix produced under $msixDir" }
Write-Host "Built: $($msix.FullName)"

# --- Locate signtool (same search order as deploy.ps1) -------------------
$signtoolCandidates = @(
    "D:\BuildCache\NuGetPackages\microsoft.windows.sdk.buildtools\10.0.26100.7705\bin\10.0.26100.0\x64\signtool.exe",
    "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.26100.7705\bin\10.0.26100.0\x64\signtool.exe"
)
$signtool = $signtoolCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $signtool) {
    $found = Get-ChildItem -Path "D:\BuildCache\NuGetPackages\microsoft.windows.sdk.buildtools" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*x64*' } | Select-Object -First 1
    if ($found) { $signtool = $found.FullName }
}
if (-not $signtool) { throw "signtool.exe not found." }

# --- Prepare staging folder ------------------------------------------------
if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$destMsix = Join-Path $StagingDir "ClaudeWidget.msix"
Copy-Item $msix.FullName $destMsix -Force

Write-Host "`n=== Signing MSIX ===" -ForegroundColor Cyan
& $signtool sign /fd SHA256 /sha1 $certThumbprint /s My $destMsix | Out-Host
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

Write-Host "`n=== Exporting public certificate ===" -ForegroundColor Cyan
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $certThumbprint }
if (-not $cert) { throw "Signing certificate $certThumbprint not found in Cert:\CurrentUser\My" }
$destCer = Join-Path $StagingDir "ClaudeUsageWidgetLocal.cer"
Export-Certificate -Cert $cert -FilePath $destCer | Out-Null
Write-Host "Exported: $destCer"

Write-Host "`n=== Staging installer files ===" -ForegroundColor Cyan
Copy-Item (Join-Path $RepoRoot "installer\PostInstall.ps1") $StagingDir -Force
Copy-Item (Join-Path $RepoRoot "installer\PreUninstall.ps1") $StagingDir -Force
Copy-Item (Join-Path $RepoRoot "ClaudeWidget\Assets\AppIcon.ico") $StagingDir -Force

Write-Host "`n=== Compiling installer (Inno Setup) ===" -ForegroundColor Cyan
$issPath = Join-Path $RepoRoot "installer\ClaudeWidgetSetup.iss"
& $iscc "/DStagingDir=$StagingDir" $issPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

$setupExe = Get-ChildItem -Path $OutputDir -Filter "ClaudeWidgetSetup.exe" | Select-Object -First 1
if (-not $setupExe) { throw "ClaudeWidgetSetup.exe was not produced under $OutputDir" }

Write-Host "`nDone. Send this file to recipients:" -ForegroundColor Green
Write-Host $setupExe.FullName -ForegroundColor Green
