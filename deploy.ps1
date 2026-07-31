# local.settings.ps1 (gitignored) supplies $CertThumbprint and optional
# $BuildCacheRoot — see local.settings.ps1.example for how to create it.
$localSettings = Join-Path $PSScriptRoot "local.settings.ps1"
if (-not (Test-Path $localSettings)) {
    throw "local.settings.ps1 not found. Copy local.settings.ps1.example to local.settings.ps1 and fill in your cert thumbprint."
}
. $localSettings

# $BuildCacheRoot (optional, see Directory.Build.props) redirects build
# output off C:. Falls back to the repo root, which reproduces MSBuild's
# native default output location when $BuildCacheRoot is unset.
$buildRoot = if ($BuildCacheRoot) { $BuildCacheRoot } else { $PSScriptRoot }

$msix     = Join-Path $buildRoot "ClaudeWidget\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\ClaudeWidget_1.0.0.0_x64_Debug.msix"
$signtoolCandidates = @(
    (Join-Path $buildRoot "NuGetPackages\microsoft.windows.sdk.buildtools\10.0.26100.7705\bin\10.0.26100.0\x64\signtool.exe"),
    "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.26100.7705\bin\10.0.26100.0\x64\signtool.exe"
)
$signtool = $signtoolCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $signtool) {
    # Hardcoded-version candidates above miss whenever a different SDK
    # BuildTools version got restored. Fall back to a recursive search
    # across every NuGet packages root that might exist, not just the one
    # under $buildRoot.
    $searchRoots = @(
        (Join-Path $buildRoot "NuGetPackages\microsoft.windows.sdk.buildtools"),
        "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools"
    ) | Where-Object { Test-Path $_ } | Select-Object -Unique
    $found = $searchRoots | ForEach-Object {
        Get-ChildItem -Path $_ -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*x64*' }
    } | Select-Object -First 1
    if ($found) { $signtool = $found.FullName }
}

# Sign using the cert already installed in the CurrentUser\My store (subject
# CN=ClaudeUsageWidgetLocal, matching Package.appxmanifest's Publisher) rather
# than a .pfx file on disk — the original .pfx this script used to reference
# no longer exists on this machine, but a matching cert+private key was
# already present in the store. IMPORTANT: Add-AppxPackage requires the cert
# to be trusted in Cert:\LocalMachine\TrustedPeople specifically (not just
# CurrentUser\TrustedPeople) — if signing succeeds but install fails with
# HRESULT 0x800B0109 ("не найдена цепочка сертификатов, не подтверждена
# доверенным поставщиком"/untrusted publisher), it means the store has the
# right subject name but the wrong thumbprint's the one that's actually
# machine-trusted. Check with:
#   Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where Subject -match ClaudeUsageWidgetLocal
# and use that thumbprint below.
#
# NOTE on $msix path: as of the localization-feature build, a clean
# bin/obj/AppPackages rebuild stopped producing a loose .msix under
# AppPackages\ClaudeWidget_1.0.0.0_x64_Debug_Test\ (that folder name lost
# its _x64 segment and now only contains a .msixbundle, not a loose .msix).
# The loose per-architecture .msix is still produced right next to the DLL
# under bin\x64\Debug\...\win-x64\, so that's what this script signs/installs
# now. If this ever changes again, verify by checking what
# `dotnet build ... -p:GenerateAppxPackageOnBuild=true` actually printed as
# its last few "-> ...msix" output lines.
$certThumbprint = $CertThumbprint

"signtool : $signtool" | Out-Host
"msix     : $msix"     | Out-Host

"[1] Signing..." | Out-Host
& $signtool sign /fd SHA256 /sha1 $certThumbprint /s My $msix | Out-Host
"signtool exit: $LASTEXITCODE" | Out-Host
if ($LASTEXITCODE -ne 0) { exit 1 }

"[2] Kill old process..." | Out-Host
Get-Process ClaudeWidget -ErrorAction SilentlyContinue | Stop-Process -Force

"[3] Remove old package..." | Out-Host
Get-AppxPackage "ClaudeWidget" -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue

"[4] Install..." | Out-Host
Add-AppxPackage -Path $msix -ForceApplicationShutdown 2>&1 | Out-Host

Get-AppxPackage "ClaudeWidget" | Select-Object Name, PackageFullName | Out-Host

"[5] Restart Widgets sidebar process..." | Out-Host
Get-Process -Name "Widgets" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "WidgetService" -ErrorAction SilentlyContinue | Stop-Process -Force

"Done." | Out-Host
