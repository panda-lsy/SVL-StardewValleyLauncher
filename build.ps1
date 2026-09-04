<#
.SYNOPSIS
SVL 跨平台打包脚本（Windows/macOS/Linux，Debug/Release 单文件发布）
.PARAMETER Config
Debug | Release | all（默认 all，同时构建 Debug + Release）
.PARAMETER Targets
windows | macos | all（默认 all）
.EXAMPLE
.\build.ps1 -Config Release -Targets windows
.\build.ps1 -Config all -Targets all
#>
param(
    [string]$Config = "all",
    [string]$Targets = "all"
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $RootDir "SVL.Avalonia\SVL.Avalonia.csproj"
$ExecutableName = "SVL.Avalonia"
$ArtifactPrefix = "SVL"
$PackageVersion = "1.2.0.0"

$HostOs = if ($IsMacOS) { "macos" } elseif ($IsLinux) { "linux" } else { "windows" }

function Resolve-ConfigList {
    param([string]$c)
    switch -Regex ($c.ToLowerInvariant()) {
        '^(all|both)$' { return @("Debug", "Release") }
        '^debug$' { return @("Debug") }
        '^release$' { return @("Release") }
        default {
            Write-Host "[error] 无效配置: $c（可选: Debug | Release | all）"
            exit 1
        }
    }
}

function Resolve-TargetList {
    param([string]$t)
    switch -Regex ($t.ToLowerInvariant()) {
        '^all$' { return @("windows", "macos") }
        '^windows$' { return @("windows") }
        '^macos$' { return @("macos") }
        default {
            Write-Host "[error] 无效目标: $t（可选: windows | macos | all）"
            exit 1
        }
    }
}

$configs = Resolve-ConfigList $Config
$targets = Resolve-TargetList $Targets

Write-Host "[config] VERSION=$PackageVersion CONFIGS=$($configs -join ',') TARGETS=$($targets -join ',') HOST=$HostOs"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[error] dotnet 未安装"
    exit 1
}

function Publish-Windows {
    param([string]$Rid, [string]$PublishConfig, [string]$OutDir)
    Write-Host "[publish] $Rid ($PublishConfig single-file)"
    dotnet publish $Project -c $PublishConfig -r $Rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $OutDir
    if ($LASTEXITCODE -ne 0) { Write-Host "[error] publish 失败: $Rid"; exit 1 }
}

function Publish-MacOS {
    param([string]$Rid, [string]$PublishConfig, [string]$OutDir)
    Write-Host "[publish] $Rid ($PublishConfig)"
    dotnet publish $Project -c $PublishConfig -r $Rid --self-contained true -o $OutDir
    if ($LASTEXITCODE -ne 0) { Write-Host "[error] publish 失败: $Rid"; exit 1 }
}

function Build-Windows {
    param([string]$Rid, [string]$PublishConfig)
    $configMarker = $PublishConfig.ToLower()
    $archName = $Rid.Substring(4)
    $artifactName = "${ArtifactPrefix}_v${PackageVersion}_${configMarker}_Windows_${archName}"
    $outBase = Join-Path $RootDir "artifacts"
    $payloadDir = Join-Path $outBase $artifactName
    $publishDir = Join-Path $outBase "${artifactName}_publish"
    $zipPath = Join-Path $outBase "${artifactName}.zip"

    Publish-Windows -Rid $Rid -PublishConfig $PublishConfig -OutDir $publishDir

    if (Test-Path $payloadDir) { Remove-Item -Recurse -Force $payloadDir }
    New-Item -ItemType Directory -Path $payloadDir | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $payloadDir -Recurse -Force

    $mainExe = Join-Path $payloadDir "${ExecutableName}.exe"
    if (-not (Test-Path $mainExe)) {
        $fallback = Get-ChildItem -Path $payloadDir -Filter *.exe -File | Select-Object -First 1
        if ($null -eq $fallback) { Write-Host "[error] 未找到 exe: $Rid"; exit 1 }
        $mainExe = $fallback.FullName
    }

    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
    Write-Host "[ok] $Rid -> $zipPath"
}

function Build-MacOS {
    param([string]$Rid, [string]$PublishConfig)
    $configMarker = $PublishConfig.ToLower()
    $archName = $Rid.Substring(4)
    $artifactName = "${ArtifactPrefix}_v${PackageVersion}_${configMarker}_macOS_${archName}"
    $outBase = Join-Path $RootDir "artifacts"
    $publishDir = Join-Path $outBase "${artifactName}_publish"
    $zipPath = Join-Path $outBase "${artifactName}.zip"

    Publish-MacOS -Rid $Rid -PublishConfig $PublishConfig -OutDir $publishDir

    if ($HostOs -ne "macos") {
        Write-Host "[warn] 非 macOS 主机，跳过 .app/.dmg 打包，仅输出 publish 目录"
        if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
        Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
        Write-Host "[ok] $Rid -> $zipPath (publish only)"
        return
    }

    $appRoot = Join-Path $outBase "${artifactName}.app"
    $appContents = Join-Path $appRoot "Contents"
    $appMacos = Join-Path $appContents "MacOS"
    $appResources = Join-Path $appContents "Resources"

    if (Test-Path $appRoot) { Remove-Item -Recurse -Force $appRoot }
    New-Item -ItemType Directory -Path $appMacos -Force | Out-Null
    New-Item -ItemType Directory -Path $appResources -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $appMacos -Recurse -Force

    $iconSrc = Join-Path $RootDir "SVL.Desktop\Images\icon.png"
    if (Test-Path $iconSrc) {
        $iconsetDir = Join-Path $env:TEMP "AppIcon.iconset"
        if (Test-Path $iconsetDir) { Remove-Item -Recurse -Force $iconsetDir }
        New-Item -ItemType Directory -Path $iconsetDir | Out-Null
        foreach ($size in @(16,32,128,256,512)) {
            & sips -z $size $size $iconSrc --out (Join-Path $iconsetDir "icon_${size}x${size}.png") 2>$null
            $double = $size * 2
            & sips -z $double $double $iconSrc --out (Join-Path $iconsetDir "icon_${size}x${size}@2x.png") 2>$null
        }
        $icnsPath = Join-Path $appResources "AppIcon.icns"
        & iconutil -c icns $iconsetDir -o $icnsPath 2>$null
        if (-not (Test-Path $icnsPath)) { Copy-Item $iconSrc (Join-Path $appResources "AppIcon.png") }
    }

    $plistPath = Join-Path $appContents "Info.plist"
    @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SVL</string>
  <key>CFBundleDisplayName</key><string>SVL</string>
  <key>CFBundleIdentifier</key><string>io.svl.launcher.$Rid</string>
  <key>CFBundleVersion</key><string>$PackageVersion</string>
  <key>CFBundleShortVersionString</key><string>$PackageVersion</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>$ExecutableName</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
</dict>
</plist>
"@ | Set-Content $plistPath -Encoding UTF8

    $dmgPath = Join-Path $outBase "${artifactName}.dmg"
    if (Test-Path $dmgPath) { Remove-Item -Force $dmgPath }
    $dmgStaging = Join-Path $outBase "${artifactName}_dmg"
    if (Test-Path $dmgStaging) { Remove-Item -Recurse -Force $dmgStaging }
    New-Item -ItemType Directory -Path $dmgStaging | Out-Null
    Copy-Item -Path $appRoot -Destination $dmgStaging -Recurse -Force
    $appsLink = Join-Path $dmgStaging "Applications"
    if (-not (Test-Path $appsLink)) { New-Item -ItemType SymbolicLink -Path $appsLink -Target "/Applications" }
    & hdiutil create -volname "SVL" -srcfolder $dmgStaging -ov -format UDZO $dmgPath 2>$null
    Write-Host "[ok] $Rid -> $dmgPath"
}

foreach ($cfg in $configs) {
    foreach ($tgt in $targets) {
        if ($tgt -eq "windows") {
            Build-Windows -Rid "win-x64" -PublishConfig $cfg
        } elseif ($tgt -eq "macos") {
            Build-MacOS -Rid "osx-arm64" -PublishConfig $cfg
            Build-MacOS -Rid "osx-x64" -PublishConfig $cfg
        }
    }
}

Write-Host "[done] 产物目录: $RootDir\artifacts"
