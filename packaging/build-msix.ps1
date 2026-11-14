# Builds the Microsoft Store package: packaging/out/SnapCull_<version>_x64.msix
#
#   powershell -File packaging/build-msix.ps1            # version taken from src/SnapCull/SnapCull.csproj
#   powershell -File packaging/build-msix.ps1 -Version 0.2.0
#
# The package is not signed: upload it in Partner Center and the Store signs it.
# MakeAppx and MakePri come from Microsoft's "Microsoft.Windows.SDK.BuildTools" NuGet package,
# downloaded once into packaging/out/tools (no Windows SDK install needed).

param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
$layout = Join-Path $out 'layout'
$tools = Join-Path $out 'tools'
$buildToolsVersion = '10.0.28000.2957'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'src\SnapCull\SnapCull.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
# Store versions have four numbers and the last one must be 0.
$parts = @($Version.Split('.') | ForEach-Object { [int]$_ })
while ($parts.Count -lt 3) { $parts += 0 }
$packageVersion = '{0}.{1}.{2}.0' -f $parts[0], $parts[1], $parts[2]
Write-Host "Packaging SnapCull $packageVersion"

# 1. Publish the app (self-contained, so users don't need .NET installed).
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
& dotnet publish (Join-Path $root 'src\SnapCull\SnapCull.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=false -p:DebugType=none -p:Version=$Version -o $layout -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2. Manifest and tile images.
(Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw).Replace('$VERSION$', $packageVersion) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding UTF8
Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $layout 'Assets') -Recurse

# 3. MakeAppx / MakePri from the SDK build-tools package.
$makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
if (-not $makeappx) {
    New-Item -ItemType Directory -Force $tools | Out-Null
    $nupkg = Join-Path $tools 'buildtools.zip'
    Write-Host "Downloading Microsoft.Windows.SDK.BuildTools $buildToolsVersion from nuget.org..."
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools/$buildToolsVersion" -OutFile $nupkg -UseBasicParsing
    Expand-Archive $nupkg (Join-Path $tools 'buildtools') -Force
    $makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
}
$makepri = Join-Path $makeappx.DirectoryName 'makepri.exe'

# 4. resources.pri, so Windows picks the right tile size for each scale and place (taskbar, Start, Store).
#    Only the Assets folder is indexed, not the app's DLLs.
$resRoot = Join-Path $out 'pri'
if (Test-Path $resRoot) { Remove-Item $resRoot -Recurse -Force }
New-Item -ItemType Directory -Force $resRoot | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $resRoot 'Assets') -Recurse
Copy-Item (Join-Path $layout 'AppxManifest.xml') $resRoot
$priConfig = Join-Path $out 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
# Keep every scale in one resources.pri: split resource packs only make sense for bundles.
$config = [xml](Get-Content $priConfig)
$packaging = $config.SelectSingleNode('//packaging')
if ($packaging) { [void]$packaging.ParentNode.RemoveChild($packaging) }
$config.Save($priConfig)
& $makepri new /pr $resRoot /cf $priConfig /mn (Join-Path $resRoot 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

# 5. Pack. MakeAppx validates the manifest against the schema as it goes.
$msix = Join-Path $out "SnapCull_$($packageVersion)_x64.msix"
& $makeappx.FullName pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

Write-Host ""
Write-Host "Built $msix ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB)"
Write-Host "Upload it in Partner Center: SnapCull > Packages."
