$cdb = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe'
$log = 'Z:\zorinconnect\cdb_output.txt'
Write-Host "cdb: $(Test-Path $cdb)"
& $cdb -plmPackage ZorinConnect.W10M_1b7q5sa4bwdpa -plmApp App -c 'g;q' > $log 2>&1
Write-Host "cdb exit: $LASTEXITCODE"
Write-Host '--- tail ---'
Get-Content $log -Tail 100
