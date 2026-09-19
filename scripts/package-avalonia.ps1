<#!
.SYNOPSIS
为 Avalonia 项目生成 Windows 单文件调试/发布包。

.DESCRIPTION
这是迁移文档约定的兼容入口，实际复用仓库根目录的 build.ps1，避免两套
发布参数长期漂移。可选择配置和输出目录；未指定输出目录时仍写入仓库的 artifacts。
#>
param(
    [ValidateSet("Debug", "Release", "all")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$buildScript = Join-Path $root "build.ps1"

if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    throw "找不到根目录打包脚本: $buildScript"
}

& $buildScript -Config $Configuration -Targets windows -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
