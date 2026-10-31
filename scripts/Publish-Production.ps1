param([string]$Dotnet='dotnet',[string]$OpenRgbDirectory='', [string]$DetectorConfig='', [string]$PawnInstaller='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'artifacts\production'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
# Never package stale runtime files left by a previous production publish.
function Reset-ArtifactDirectory([string]$directory){
 $artifacts=[IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
 $full=[IO.Path]::GetFullPath($directory)
 if($full -notin @((Join-Path $artifacts 'production'),(Join-Path $artifacts 'installer'))){throw 'Unexpected artifact directory'}
 function Assert-NoReparse([string]$path){
  $current=$path
  while($current){
   if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Reparse point in artifact path: $current"}
   $current=Split-Path -Parent $current
  }
  if(Test-Path -LiteralPath $path){
   foreach($item in Get-ChildItem -LiteralPath $path -Force){
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point in artifact tree: $($item.FullName)"}
    if($item.PSIsContainer){Assert-NoReparse $item.FullName}
   }
  }
 }
 Assert-NoReparse $full
 if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force}
 New-Item -ItemType Directory -Path $full -Force | Out-Null
}
# Reject inputs inside outputs before cleaning, so callers do not lose their only dependency copy.
foreach($inputPath in @($OpenRgbDirectory,$DetectorConfig,$PawnInstaller)){
 if($inputPath){
  $fullInput=[IO.Path]::GetFullPath($inputPath)
  foreach($destination in @($out,(Join-Path $root 'artifacts\installer'))){
   $fullDestination=[IO.Path]::GetFullPath($destination)
   if($fullInput.Equals($fullDestination,[StringComparison]::OrdinalIgnoreCase) -or $fullInput.StartsWith($fullDestination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Dependency inputs must be staged outside production and installer outputs before publishing'}
  }
 }
}
Reset-ArtifactDirectory $out
Reset-ArtifactDirectory (Join-Path $root 'artifacts\installer')
function Publish([string]$project,[string]$destination){
 & $Dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -p:Version=0.2.0 -o $destination --nologo
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
 $manifest=@{id=$p.id;name=$p.folder;version='0.2.0';protocolMajor=1;entryPoint=$p.exe;arguments=@($p.args);permissions=@($p.permissions);methods=@('device.discover','device.execute','telemetry.read','device.release');enabled=$true} | ConvertTo-Json -Depth 5
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
foreach($folder in @('en','zh-CN','images')){
 $source=Join-Path $root ('docs\'+$folder)
 if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $docs $folder) -Recurse -Force}
}
foreach($file in @('README.md','README.zh-CN.md','LICENSE','THIRD-PARTY-NOTICES.md')){if(Test-Path (Join-Path $root $file)){Copy-Item (Join-Path $root $file) $out -Force}}
if(Test-Path (Join-Path $root 'third-party\licenses')){Copy-Item (Join-Path $root 'third-party\licenses') (Join-Path $out 'licenses') -Recurse -Force}
$package=Join-Path $root 'artifacts\package';New-Item -ItemType Directory -Path $package -Force | Out-Null
$zip=Join-Path $package 'payload.zip'
if(Test-Path $zip){Remove-Item $zip}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
Publish 'src\LightOrder.Setup\LightOrder.Setup.csproj' (Join-Path $root 'artifacts\installer')
& (Join-Path $root 'artifacts\installer\LightOrder.exe') --self-test
if($LASTEXITCODE -ne 0){throw 'Installer lifecycle self-test failed'}
Get-FileHash (Join-Path $root 'artifacts\installer\LightOrder.exe') -Algorithm SHA256 | Format-List
