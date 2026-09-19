<#
.SYNOPSIS
SVL 跨平台打包脚本（Windows/macOS/Linux，Debug/Release 单文件发布）
.PARAMETER Config
Debug | Release | all（默认 all，同时构建 Debug + Release）
.PARAMETER Targets
windows | linux | macos | all（默认 all）
.PARAMETER OutputDirectory
产物输出目录；可为绝对路径或相对于仓库根目录的路径，默认 <仓库根目录>/artifacts
.EXAMPLE
.\build.ps1 -Config Release -Targets windows
.\build.ps1 -Config Debug -Targets windows -OutputDirectory $env:TEMP\SVL-Debug
.\build.ps1 -Config all -Targets all
#>
param(
    [string]$Config = "all",
    [string]$Targets = "all",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path (Join-Path $RootDir "SVL.Avalonia") "SVL.Avalonia.csproj"
$ArtifactRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $RootDir "artifacts"
} elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $RootDir $OutputDirectory))
}
$ExecutableName = "SVL.Avalonia"
$ArtifactPrefix = "SVL"
$PackageVersion = "1.2.0.0"
$MacOSMinimumSystemVersion = "14.0"

function Normalize-PathForComparison {
    param([string]$Path)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $separators = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $trimmedPath = $fullPath.TrimEnd($separators)
    if ([string]::IsNullOrWhiteSpace($trimmedPath)) {
        return [System.IO.Path]::GetPathRoot($fullPath)
    }

    return $trimmedPath
}

$normalizedArtifactRoot = Normalize-PathForComparison $ArtifactRoot
$normalizedRepoRoot = Normalize-PathForComparison $RootDir
$normalizedFilesystemRoot = Normalize-PathForComparison ([System.IO.Path]::GetPathRoot($ArtifactRoot))
$pathComparison = if ($env:OS -eq "Windows_NT") {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}
if ([string]::Equals(
        $normalizedArtifactRoot,
        $normalizedFilesystemRoot,
        $pathComparison) -or
    [string]::Equals(
        $normalizedArtifactRoot,
        $normalizedRepoRoot,
        $pathComparison)) {
    throw "产物目录不能是文件系统根目录或仓库根目录: $ArtifactRoot"
}
New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null

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
        '^all$' { return @("windows", "linux", "macos") }
        '^windows$' { return @("windows") }
        '^linux$' { return @("linux") }
        '^macos$' { return @("macos") }
        default {
            Write-Host "[error] 无效目标: $t（可选: windows | linux | macos | all）"
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
    $outBase = $ArtifactRoot
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

function Build-Linux {
    param([string]$Rid, [string]$PublishConfig)
    $configMarker = $PublishConfig.ToLower()
    $archName = $Rid.Substring(6)
    $artifactName = "${ArtifactPrefix}_v${PackageVersion}_${configMarker}_Linux_${archName}"
    $outBase = $ArtifactRoot
    $payloadDir = Join-Path $outBase $artifactName
    $publishDir = Join-Path $outBase "${artifactName}_publish"
    $zipPath = Join-Path $outBase "${artifactName}.zip"

    Write-Host "[publish] $Rid ($PublishConfig single-file)"
    dotnet publish $Project -c $PublishConfig -r $Rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { Write-Host "[error] publish 失败: $Rid"; exit 1 }

    $mainApp = Join-Path $publishDir $ExecutableName
    if (-not (Test-Path -LiteralPath $mainApp -PathType Leaf)) {
        Write-Host "[error] 未找到 Linux apphost: $mainApp"
        exit 1
    }

    if (Test-Path -LiteralPath $payloadDir) { Remove-Item -Recurse -Force -LiteralPath $payloadDir }
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $payloadDir -Recurse -Force

    # Windows 创建的 ZIP 不保留 Unix executable bit。提供一个可通过 `sh run-svl.sh`
    # 启动的入口，在运行时给 apphost 补上执行权限。
    $launcherScript = Join-Path $payloadDir "run-svl.sh"
    $launcherContent = @'
#!/usr/bin/env sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
chmod +x "$script_dir/__APPHOST__"
exec "$script_dir/__APPHOST__" "$@"
'@
    $launcherContent = $launcherContent.Replace("__APPHOST__", $ExecutableName) + "`n"
    [System.IO.File]::WriteAllText(
        $launcherScript,
        $launcherContent,
        [System.Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -Force -LiteralPath $zipPath }
    Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
    Write-Host "[ok] $Rid -> $zipPath"
}

function Build-MacOS {
    param([string]$Rid, [string]$PublishConfig)
    $configMarker = $PublishConfig.ToLower()
    $archName = $Rid.Substring(4)
    $artifactName = "${ArtifactPrefix}_v${PackageVersion}_${configMarker}_macOS_${archName}"
    $outBase = $ArtifactRoot
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
    $bundleExecutable = Join-Path $appMacos $ExecutableName
    if (-not (Test-Path -LiteralPath $bundleExecutable -PathType Leaf)) {
        Write-Host "[error] macOS apphost 不存在: $bundleExecutable"
        exit 1
    }
    & chmod +x $bundleExecutable
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[error] 无法设置 macOS apphost 执行权限: $bundleExecutable"
        exit 1
    }

    $iconSrc = Join-Path (Join-Path (Join-Path $RootDir "SVL.Desktop") "Images") "icon.png"
    if (Test-Path $iconSrc) {
        $iconsetDir = Join-Path ([System.IO.Path]::GetTempPath()) ("SVL-AppIcon-" + [guid]::NewGuid().ToString("N") + ".iconset")
        New-Item -ItemType Directory -Path $iconsetDir | Out-Null
        try {
            foreach ($size in @(16,32,128,256,512)) {
                & sips -z $size $size $iconSrc --out (Join-Path $iconsetDir "icon_${size}x${size}.png") 2>$null
                $double = $size * 2
                & sips -z $double $double $iconSrc --out (Join-Path $iconsetDir "icon_${size}x${size}@2x.png") 2>$null
            }
            $icnsPath = Join-Path $appResources "AppIcon.icns"
            & iconutil -c icns $iconsetDir -o $icnsPath 2>$null
            if (-not (Test-Path $icnsPath)) { Copy-Item $iconSrc (Join-Path $appResources "AppIcon.png") }
        } finally {
            Remove-Item -LiteralPath $iconsetDir -Recurse -Force -ErrorAction SilentlyContinue
        }
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
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>Nexus Mods Protocol</string>
      <key>CFBundleURLSchemes</key>
      <array><string>nxm</string></array>
    </dict>
  </array>
  <key>LSMinimumSystemVersion</key><string>$MacOSMinimumSystemVersion</string>
</dict>
</plist>
"@ | Set-Content $plistPath -Encoding UTF8

    & plutil -lint $plistPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[error] macOS Info.plist 校验失败: $plistPath"
        exit 1
    }
    $registeredScheme = & /usr/libexec/PlistBuddy -c 'Print :CFBundleURLTypes:0:CFBundleURLSchemes:0' $plistPath
    if ($LASTEXITCODE -ne 0 -or $registeredScheme.Trim() -ne "nxm") {
        Write-Host "[error] macOS app bundle 未声明 nxm URL scheme: $plistPath"
        exit 1
    }

    $dmgPath = Join-Path $outBase "${artifactName}.dmg"
    if (Test-Path $dmgPath) { Remove-Item -Force $dmgPath }
    $dmgStaging = Join-Path $outBase "${artifactName}_dmg"
    if (Test-Path $dmgStaging) { Remove-Item -Recurse -Force $dmgStaging }
    New-Item -ItemType Directory -Path $dmgStaging | Out-Null
    Copy-Item -Path $appRoot -Destination $dmgStaging -Recurse -Force
    $appsLink = Join-Path $dmgStaging "Applications"
    if (-not (Test-Path $appsLink)) { New-Item -ItemType SymbolicLink -Path $appsLink -Target "/Applications" }
    & hdiutil create -volname "SVL" -srcfolder $dmgStaging -ov -format UDZO $dmgPath 2>$null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dmgPath -PathType Leaf)) {
        Write-Host "[error] macOS DMG 创建失败: $dmgPath"
        exit 1
    }
    Write-Host "[ok] $Rid -> $dmgPath"
}

foreach ($cfg in $configs) {
    foreach ($tgt in $targets) {
        if ($tgt -eq "windows") {
            Build-Windows -Rid "win-x64" -PublishConfig $cfg
        } elseif ($tgt -eq "linux") {
            Build-Linux -Rid "linux-x64" -PublishConfig $cfg
        } elseif ($tgt -eq "macos") {
            Build-MacOS -Rid "osx-arm64" -PublishConfig $cfg
            Build-MacOS -Rid "osx-x64" -PublishConfig $cfg
        }
    }
}

Write-Host "[done] 产物目录: $ArtifactRoot"
