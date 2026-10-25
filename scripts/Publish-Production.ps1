param([string]$Dotnet='dotnet',[string]$OpenRgbDirectory='', [string]$DetectorConfig='', [string]$PawnInstaller='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'artifacts\production'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
function Publish([string]$project,[string]$destination){
 & $Dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $destination --nologo
 if($LASTEXITCODE -ne 0){throw "Publish failed: $project"}
}
Publish 'src\LightOrder.App\LightOrder.App.csproj' $out
Publish 'src\LightOrder.Service\LightOrder.Service.csproj' $out
$plugins=@(
 @{folder='GigabyteIt5702';project='GigabyteIt5702';id='local.gigabyte-it5702';exe='ControlCenter.GigabyteIt5702.exe';args=@();permissions=@('hid:048d:5702')},
 @{folder='OpenRgb';project='ControlCenter.OpenRgb';id='local.openrgb';exe='ControlCenter.OpenRgb.exe';args=@();permissions=@('loopback:16742','process:OpenRGB')},
 @{folder='HardwareMonitor';project='HardwareMonitor';id='local.hardware-monitor';exe='ControlCenter.HardwareMonitor.exe';args=@();permissions=@('hardware-monitor','fan-control')},
 @{folder='WindowsPolicy';project='ControlCenter.WindowsPolicy';id='local.windows-policy';exe='ControlCenter.WindowsPolicy.exe';args=@('--service');permissions=@('windows-policy','system-info')}
)
foreach($p in $plugins){
 $dir=Join-Path $out ('plugins\'+$p.folder)
 Publish ('plugins\'+$p.folder+'\'+$p.project+'.csproj') $dir
 $manifest=@{id=$p.id;name=$p.folder;version='0.1.0';protocolMajor=1;entryPoint=$p.exe;arguments=@($p.args);permissions=@($p.permissions);methods=@('device.discover','device.execute','telemetry.read','device.release');enabled=$true} | ConvertTo-Json -Depth 5
 [IO.File]::WriteAllText((Join-Path $dir 'plugin.json'),$manifest,[Text.UTF8Encoding]::new($false))
}
if($OpenRgbDirectory){
 if(-not (Test-Path (Join-Path $OpenRgbDirectory 'OpenRGB.exe'))){throw 'OpenRGB directory missing executable'}
 $target=Join-Path $out 'plugins\OpenRgb\runtime'; New-Item -ItemType Directory -Path $target -Force | Out-Null
 Copy-Item (Join-Path $OpenRgbDirectory '*') $target -Recurse -Force
}
if($DetectorConfig){Copy-Item $DetectorConfig (Join-Path $out 'plugins\OpenRgb\detectors.json') -Force}
if($PawnInstaller){
 if((Get-AuthenticodeSignature $PawnInstaller).Status -ne 'Valid'){throw 'PawnIO signature invalid'}
 New-Item -ItemType Directory -Path (Join-Path $out 'dependencies') -Force | Out-Null
 Copy-Item $PawnInstaller (Join-Path $out 'dependencies\PawnIO_setup.exe') -Force
}
$docs=Join-Path $out 'docs';New-Item -ItemType Directory -Path $docs -Force | Out-Null
foreach($file in @('README.md','README.zh-CN.md','LICENSE','THIRD-PARTY-NOTICES.md')){if(Test-Path (Join-Path $root $file)){Copy-Item (Join-Path $root $file) $out -Force}}
if(Test-Path (Join-Path $root 'third-party\licenses')){Copy-Item (Join-Path $root 'third-party\licenses') (Join-Path $out 'licenses') -Recurse -Force}
$package=Join-Path $root 'artifacts\package';New-Item -ItemType Directory -Path $package -Force | Out-Null
$zip=Join-Path $package 'payload.zip'
if(Test-Path $zip){Remove-Item $zip}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
Publish 'src\LightOrder.Setup\LightOrder.Setup.csproj' (Join-Path $root 'artifacts\installer')
Get-FileHash (Join-Path $root 'artifacts\installer\LightOrder.exe') -Algorithm SHA256 | Format-List
