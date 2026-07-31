Get-Process | Where-Object { $_.Name -like '*ClaudeWidget*' } | ForEach-Object {
    Write-Host "Killing $($_.Name) ($($_.Id))"
    Stop-Process -Id $_.Id -Force
}
Write-Host "Done"
