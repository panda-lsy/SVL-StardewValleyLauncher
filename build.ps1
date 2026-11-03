<#
.SYNOPSIS
SVL 跨平台打包脚本（Windows/macOS/Linux，Debug/Release 发布）
.PARAMETER Config
Debug | Release | all（默认 all，同时构建 Debug + Release）
.PARAMETER Targets
windows | linux | macos | all（默认 all）
.PARAMETER OutputDirectory
产物输出目录；可为绝对路径或相对于仓库根目录的路径，默认 <仓库根目录>/artifacts
.PARAMETER FrameworkDependent
构建需要用户预装对应架构 .NET 10 Runtime 的较小程序包。
.EXAMPLE
.\build.ps1 -Config Release -Targets windows
.\build.ps1 -Config Debug -Targets windows -OutputDirectory $env:TEMP\SVL-Debug
.\build.ps1 -Config all -Targets all
#>
param(
    [string]$Config = "all",
    [string]$Targets = "all",
    [string]$OutputDirectory = "",
    [switch]$FrameworkDependent
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

$configs = @(Resolve-ConfigList $Config)
$targetList = @(Resolve-TargetList $Targets)
Write-Host "[config] VERSION=$PackageVersion CONFIGS=$($configs -join ',') TARGETS=$($targetList -join ',') HOST=$HostOs FRAMEWORK_DEPENDENT=$FrameworkDependent"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[error] dotnet 未安装"
    exit 1
}

function Publish-Windows {
    param([string]$Rid, [string]$PublishConfig, [string]$OutDir)
    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    # .NET 不允许压缩依赖外部运行时的单文件；ZIP 仍会压缩它的内容。
    $compressBundle = if ($FrameworkDependent) { "false" } else { "true" }
    Write-Host "[publish] $Rid ($PublishConfig single-file, self-contained=$selfContained)"
    dotnet publish $Project -c $PublishConfig -r $Rid --self-contained $selfContained `
        -p:PublishSelfContained=$selfContained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$compressBundle `
        -t:Rebuild `
        -o $OutDir
    if ($LASTEXITCODE -ne 0) { Write-Host "[error] publish 失败: $Rid"; exit 1 }
}

function Publish-MacOS {
    param([string]$Rid, [string]$PublishConfig, [string]$OutDir)
    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    Write-Host "[publish] $Rid ($PublishConfig, self-contained=$selfContained)"
    dotnet publish $Project -c $PublishConfig -r $Rid --self-contained $selfContained `
        -p:PublishSelfContained=$selfContained -o $OutDir
    if ($LASTEXITCODE -ne 0) { Write-Host "[error] publish 失败: $Rid"; exit 1 }
}

function Build-Windows {
    param([string]$Rid, [string]$PublishConfig)
    $configMarker = $PublishConfig.ToLower()
    $archName = $Rid.Substring(4)
    $artifactName = "${ArtifactPrefix}_v${PackageVersion}_${configMarker}_Windows_${archName}"
    if ($FrameworkDependent) { $artifactName += "_requires_dotnet10" }
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

    if (-not $FrameworkDependent -and $Rid -eq "win-x64") {
        # 旧 WPF 更新器把发布资产直接复制为 EXE，因此必须单独发布真正的单文件 EXE。
        # 轻量包不带运行时，不能冒充旧用户可直接升级的自包含版本。
        $legacyUpdateExe = Join-Path $outBase "SVL.Desktop_v${PackageVersion}_${PublishConfig}.exe"
        Copy-Item -LiteralPath $mainExe -Destination $legacyUpdateExe -Force
        Write-Host "[ok] WPF 升级兼容文件 -> $legacyUpdateExe"
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
    if ($FrameworkDependent) { $artifactName += "_requires_dotnet10" }
    $outBase = $ArtifactRoot
    $payloadDir = Join-Path $outBase $artifactName
    $publishDir = Join-Path $outBase "${artifactName}_publish"
    $zipPath = Join-Path $outBase "${artifactName}.zip"

    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    $compressBundle = if ($FrameworkDependent) { "false" } else { "true" }
    Write-Host "[publish] $Rid ($PublishConfig single-file, self-contained=$selfContained)"
    dotnet publish $Project -c $PublishConfig -r $Rid --self-contained $selfContained `
        -p:PublishSelfContained=$selfContained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$compressBundle `
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
    # PowerShell preserves the source file's line endings in here-strings.
    # A Linux launcher generated on Windows must always contain Unix newlines.
    $launcherContent = $launcherContent.Replace("`r`n", "`n").Replace("`r", "`n")
    $launcherContent = $launcherContent.Replace("__APPHOST__", $ExecutableName) + "`n"
    [System.IO.File]::WriteAllText(
        $launcherScript,
        $launcherContent,
        [System.Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -Force -LiteralPath $zipPath }
    Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
    Write-Host "[ok] $Rid -> $zipPath"

    if ($HostOs -eq "linux" -and $Rid -eq "linux-x64") {
        if (Get-Command dpkg-deb -ErrorAction SilentlyContinue) {
            $debVersion = if ($PublishConfig -eq "Debug") { "${PackageVersion}~debug" } else { $PackageVersion }
            $debMode = if ($FrameworkDependent) { "framework-dependent" } else { "self-contained" }
            & sh (Join-Path $RootDir "scripts/package-linux-deb.sh") $zipPath $outBase $debVersion $debMode
            if ($LASTEXITCODE -ne 0) { throw "Linux DEB 打包失败" }
        } else {
            Write-Host "[warn] 未找到 dpkg-deb，跳过 Ubuntu/Debian 安装包"
        }
    }
}

function Build-MacOS {
    param([string]$Rid, [string]$PublishConfig)
    $configMarker = $PublishConfig.ToLower()
    $archName = $Rid.Substring(4)
    $artifactName = "${ArtifactPrefix}_v${PackageVersion}_${configMarker}_macOS_${archName}"
    if ($FrameworkDependent) { $artifactName += "_requires_dotnet10" }
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

    $iconSrc = Join-Path (Join-Path (Join-Path $RootDir "SVL.Avalonia") "Assets") "Icons\icon.png"
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
  <key>CFBundleDisplayName</key><string>Stardew Valley Launcher</string>
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

    # .NET apphost 自带的签名没有包含新创建的 Info.plist 和资源，必须在
    # app bundle 内容全部写入后重新签名；ad-hoc 签名不等于开发者签名或公证。
    & codesign --force --deep --sign - $appRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[error] macOS app bundle 签名失败: $appRoot"
        exit 1
    }
    & codesign --verify --deep --strict $appRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[error] macOS app bundle 签名校验失败: $appRoot"
        exit 1
    }

    $dmgPath = Join-Path $outBase "${artifactName}.dmg"
    if (Test-Path $dmgPath) { Remove-Item -Force $dmgPath }
    $dmgStaging = Join-Path $outBase "${artifactName}_dmg"
    if (Test-Path $dmgStaging) { Remove-Item -Recurse -Force $dmgStaging }
    New-Item -ItemType Directory -Path $dmgStaging | Out-Null
    Copy-Item -LiteralPath $appRoot -Destination (Join-Path $dmgStaging "Stardew Valley Launcher.app") -Recurse
    $appsLink = Join-Path $dmgStaging "Applications"
    New-Item -ItemType SymbolicLink -Path $appsLink -Target "/Applications" | Out-Null
    $backgroundSource = Join-Path $RootDir "packaging/macos/installer-background.png"
    $finderLayout = Join-Path $RootDir "packaging/macos/installer-layout.dsstore"
    if (-not (Test-Path -LiteralPath $backgroundSource -PathType Leaf) -or
        -not (Test-Path -LiteralPath $finderLayout -PathType Leaf)) {
        throw "缺少 macOS 安装背景或 Finder 布局文件"
    }
    $backgroundDirectory = Join-Path $dmgStaging ".background"
    New-Item -ItemType Directory -Path $backgroundDirectory | Out-Null
    $background = Join-Path $backgroundDirectory "installer.png"
    & sips -z 440 660 $backgroundSource --out $background | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $background -PathType Leaf)) {
        throw "无法生成 macOS 安装背景"
    }

    # Finder 布局是预先生成的 .DS_Store，不需要 GUI 会话或自动化授权，
    # 因而本机和 CI 都可生成同样的背景与可拖拽图标。
    Copy-Item -LiteralPath $finderLayout -Destination (Join-Path $dmgStaging ".DS_Store")
    & hdiutil create -quiet -volname "SVL Installer" -fs HFS+ -srcfolder $dmgStaging -format UDZO $dmgPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dmgPath -PathType Leaf)) {
        throw "macOS DMG 创建失败: $dmgPath"
    }
    Write-Host "[ok] $Rid -> $dmgPath"
}

foreach ($cfg in $configs) {
    foreach ($tgt in $targetList) {
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
