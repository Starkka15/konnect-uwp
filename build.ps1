# Zorin Connect W10M — build Release|ARM + pack signed .appx
# Run on the VM. Source of truth = Z:\zorinconnect, build happens on C: (msbuild/NuGet unhappy on network shares).
param(
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$src      = 'Z:\zorinconnect'
$work     = 'C:\Users\Starkka15\source\repos\ZorinConnect.UWP'
$proj     = "$work\ZorinConnect.UWP\ZorinConnect.UWP.csproj"
$ilcDir   = "$work\ZorinConnect.UWP\bin\ARM\$Configuration\ilc"
$winkits  = 'C:\Program Files (x86)\Windows Kits\10'
$makeappx = "$winkits\bin\10.0.16299.0\x64\makeappx.exe"
$signtool = "$winkits\bin\10.0.16299.0\x64\signtool.exe"
$pfxPath  = "$work\ZorinConnect.UWP\ZorinConnect_TemporaryKey.pfx"
$layout   = "$work\AppXLayoutARM"
$appxOut  = 'Z:\zorinconnect\ZorinConnect_ARM.appx'

Write-Host "=== Sync Z: -> C: ==="
robocopy "$src\ZorinConnect.UWP" "$work\ZorinConnect.UWP" /MIR /XD bin obj /NFL /NDL /NJH | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed ($LASTEXITCODE)"; exit 1 }
Copy-Item "$src\ZorinConnect.UWP.sln" "$work\" -Force

Write-Host "=== Locate msbuild ==="
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) { Write-Error 'msbuild not found'; exit 1 }
Write-Host "msbuild: $msbuild"

Write-Host "=== Restore ==="
& $msbuild $proj /t:Restore /p:Configuration=$Configuration /p:Platform=ARM /v:m
if ($LASTEXITCODE -ne 0) { Write-Error 'restore failed'; exit 1 }

Write-Host "=== Build $Configuration|ARM ==="
& $msbuild $proj /t:Build /p:Configuration=$Configuration /p:Platform=ARM /p:AppxPackageSigningEnabled=false /v:m
if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 1 }

if ($Configuration -ne 'Release') { Write-Host 'Debug build done (no pack).'; exit 0 }

Write-Host "=== Pack layout from ilc\ ==="
if (-not (Test-Path "$ilcDir\ZorinConnect.exe")) { Write-Error "ilc output missing at $ilcDir"; exit 1 }
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item $layout -ItemType Directory | Out-Null

# Whole ilc dir minus pdb/ folders msbuild junk; manifest gets Debug-VCLibs dependency stripped
robocopy $ilcDir $layout /E /XF *.pdb *.g.cs DotNetNative.debugger-services-def.json /NFL /NDL /NJH | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Error "layout robocopy failed"; exit 1 }
$manifest = Get-Content "$layout\AppxManifest.xml" -Raw
$manifest = $manifest -replace '\s*<PackageDependency Name="Microsoft\.VCLibs\.140\.00\.Debug"[^/]*/>', ''
Set-Content "$layout\AppxManifest.xml" $manifest -Encoding UTF8

Write-Host "=== makeappx ==="
& $makeappx pack /d $layout /p $appxOut /o
if ($LASTEXITCODE -ne 0) { Write-Error 'MakeAppx failed'; exit 1 }

Write-Host "=== sign ==="
& $signtool sign /fd SHA256 /f $pfxPath /p DevOnly $appxOut
if ($LASTEXITCODE -ne 0) { Write-Error 'SignTool failed'; exit 1 }

Write-Host "DONE. APPX: $appxOut"
