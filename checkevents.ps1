Write-Host '=== Application log: last 15 min errors (unfiltered) ==='
Get-WinEvent -LogName Application -MaxEvents 100 -ErrorAction SilentlyContinue |
    Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-15) -and $_.LevelDisplayName -in @('Error','Warning') } |
    Select-Object TimeCreated, ProviderName, Id, @{n='Msg';e={$_.Message.Substring(0,[Math]::Min(600,$_.Message.Length))}} | Format-List

Write-Host '=== AppModel-Runtime all recent ==='
Get-WinEvent -LogName 'Microsoft-Windows-AppModel-Runtime/Admin' -MaxEvents 20 -ErrorAction SilentlyContinue |
    Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-15) } |
    Select-Object TimeCreated, LevelDisplayName, @{n='Msg';e={$_.Message.Substring(0,[Math]::Min(300,$_.Message.Length))}} | Format-List

Write-Host '=== TWinUI / Immersive-Shell ==='
Get-WinEvent -LogName 'Microsoft-Windows-TWinUI/Operational' -MaxEvents 20 -ErrorAction SilentlyContinue |
    Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-15) } |
    Select-Object TimeCreated, @{n='Msg';e={$_.Message.Substring(0,[Math]::Min(300,$_.Message.Length))}} | Format-List

Write-Host '=== Frameworks on VM ==='
Get-AppxPackage | Where-Object { $_.Name -match 'NET.Native|CoreRuntime|VCLibs' } | Select-Object Name, Version, Architecture | Format-Table -AutoSize
