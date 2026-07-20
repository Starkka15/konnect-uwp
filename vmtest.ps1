# Crash-repro harness: build Release|x86 (.NET Native, same ILC pipeline as ARM),
# register loose on the VM, launch, report process state + WER + AppModel events + StartupTrace.
$ErrorActionPreference = 'Continue'

$src   = 'Z:\zorinconnect'
$work  = 'C:\Users\Starkka15\source\repos\ZorinConnect.UWP'
$proj  = "$work\ZorinConnect.UWP\ZorinConnect.UWP.csproj"
$ilc   = "$work\ZorinConnect.UWP\bin\x86\Release\ilc"
$pfn   = 'ZorinConnect.W10M_1b7q5sa4bwdpa'
$praid = "$pfn!App"

Write-Host "=== Sync ==="
robocopy "$src\ZorinConnect.UWP" "$work\ZorinConnect.UWP" /MIR /XD bin obj /NFL /NDL /NJH | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1

Write-Host "=== Build Release|x86 ==="
& $msbuild $proj /t:Restore /p:Configuration=Release /p:Platform=x86 /v:q
& $msbuild $proj /t:Build /p:Configuration=Release /p:Platform=x86 /p:AppxPackageSigningEnabled=false /v:m | Select-String -Pattern 'error|-> C:'
if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 1 }

Write-Host "=== Enable dumps ==="
$dumpPath = 'C:\CrashDumps'
New-Item $dumpPath -ItemType Directory -Force | Out-Null
$regPath = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\ZorinConnect.exe'
New-Item $regPath -Force | Out-Null
Set-ItemProperty $regPath -Name DumpType  -Value 2 -Type DWord
Set-ItemProperty $regPath -Name DumpCount -Value 3 -Type DWord
Set-ItemProperty $regPath -Name DumpFolder -Value $dumpPath -Type ExpandString

Write-Host "=== Register loose ==="
Get-AppxPackage -Name 'ZorinConnect.W10M' | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Register "$ilc\AppxManifest.xml"
if (-not $?) { Write-Error 'register failed'; exit 1 }

Write-Host "=== Launch ==="
$start = Get-Date
Start-Process 'explorer.exe' "shell:AppsFolder\$praid"
Start-Sleep -Seconds 6

$proc = Get-Process ZorinConnect -ErrorAction SilentlyContinue
if ($proc) { Write-Host "ALIVE: PID=$($proc.Id)" } else { Write-Host 'DEAD: process not running' }

Write-Host "`n=== Crash dumps ==="
Get-ChildItem $dumpPath -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $start } | Format-Table Name, Length -AutoSize

Write-Host "`n=== AppModel activation errors ==="
Get-WinEvent -LogName 'Microsoft-Windows-AppModel-Runtime/Admin' -MaxEvents 30 -ErrorAction SilentlyContinue |
    Where-Object { $_.TimeCreated -gt $start } | Select-Object TimeCreated, LevelDisplayName, Message | Format-List

Write-Host "`n=== WER app crash events ==="
Get-WinEvent -LogName 'Application' -MaxEvents 60 -ErrorAction SilentlyContinue |
    Where-Object { $_.TimeCreated -gt $start -and ($_.Message -match 'ZorinConnect') } |
    Select-Object TimeCreated, Id, Message | Format-List

Write-Host "`n=== StartupTrace (settings.dat) ==="
$settings = "$env:LOCALAPPDATA\Packages\$pfn\Settings\settings.dat"
if (Test-Path $settings) {
    reg load HKLM\zctmp $settings 2>&1 | Out-Null
    reg query HKLM\zctmp 2>&1
    reg query HKLM\zctmp /s /f trace 2>&1 | Select-String -Pattern 'trace' -Context 0,1
    reg unload HKLM\zctmp 2>&1 | Out-Null
} else { Write-Host "no settings.dat at $settings" }
