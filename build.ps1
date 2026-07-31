# Optional: local.settings.ps1 (gitignored) may supply $BuildCacheRoot to
# redirect build output off C: — see local.settings.ps1.example. Not
# required for a plain build; if absent or empty, standard defaults apply.
$localSettings = Join-Path $PSScriptRoot "local.settings.ps1"
if (Test-Path $localSettings) {
    . $localSettings
    if ($BuildCacheRoot) { $env:ClaudeWidgetBuildCacheRoot = $BuildCacheRoot }
}

dotnet build "ClaudeWidget/ClaudeWidget.csproj" -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:UapAppxPackageBuildMode=SideloadOnly
