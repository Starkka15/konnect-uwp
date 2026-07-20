@echo off
powershell -NoProfile -ExecutionPolicy Bypass -Command "& 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe' -plmPackage ZorinConnect.W10M_1b7q5sa4bwdpa -plmApp App -c 'g;q' *> Z:\zorinconnect\cdb_output.txt; Get-Content Z:\zorinconnect\cdb_output.txt | Select-Object -Last 80"
