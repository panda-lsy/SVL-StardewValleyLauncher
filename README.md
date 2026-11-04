<p align="center">
  <img src="SVL.Avalonia/Assets/Icons/icon.png" alt="SVL Logo" width="128" height="128">
</p>

<h1 align="center">Stardew Valley Launcher</h1>

<p align="center">
  <b>一站式星露谷物语启动器 · Mod 管理器 · Modpack 工具</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-purple?logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/Avalonia-11.2-blueviolet" alt="Avalonia">
  <img src="https://img.shields.io/badge/License-Apache_2.0-green" alt="License">
  <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-0078D6" alt="Cross-Platform">
  <img src="https://img.shields.io/github/v/release/panda-lsy/SVL-StardewValleyLauncher?include_prereleases" alt="Release">
  <img src="https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.svl.qzz.io%2Fapi%2Fstats%2Fbadges%2Fusers-total.json" alt="SVL 用户总量">
  <img src="https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.svl.qzz.io%2Fapi%2Fstats%2Fbadges%2Fusers-daily.json" alt="SVL 近 24 小时活跃用户">
  <a href="https://svl.qzz.io" target="_blank">
    <img src="https://img.shields.io/badge/官网-SVL%20Website-2F855A?style=flat-square" alt="Official Website">
  </a>
  <a href="https://svl.qzz.io/contribute.html" target="_blank">
    <img src="https://img.shields.io/badge/社区本地化-参与贡献-0F766E?style=flat-square" alt="Community Localization">
  </a>
  <a href="https://wiki.svl.qzz.io/" target="_blank">
    <img src="https://img.shields.io/badge/📖_查看_Wiki-使用教程-4A90D9?style=flat-square" alt="Wiki">
  </a>
  <a href="https://ifdian.net/a/mcshengxia" target="_blank">
    <img src="https://img.shields.io/badge/爱发电-赞助支持-FF6B6B?logo=love&logoColor=white" alt="Sponsor">
  </a>
</p>

---

## 关于

**SVL (Stardew Valley Launcher)** 是一个功能完整的星露谷物语启动器和 Mod 管理器。基于 Avalonia UI 和 .NET 10 构建，提供跨平台的现代化桌面体验，让你轻松管理游戏实例、安装和组织 Mod、集成 NexusMods / CurseForge 下载，以及创建和分享 Modpack。

## 功能亮点

### 🎮 游戏启动

- 自动检测 Stardew Valley 安装路径（Steam / GOG）
- 多游戏实例管理与版本隔离
- SMAPI 自动检测、安装与版本管理
- 支持原版与 Mod 模式启动
- 自定义窗口标题与启动参数
- 启动器自更新

### 📦 Mod 管理

- 从 ZIP 文件或文件夹安装 Mod
- 一键启用 / 禁用 / 卸载
- 智能依赖解析（拓扑排序 + 循环检测）
- 冲突检测（ID 重复、文件冲突、依赖冲突）
- Mod 详情查看（版本、作者、描述、依赖关系、文件列表）
- Mod 批量更新检查
- 社区汉化支持（GitHub + Gitee 双源）

### 🌐 远程资源集成

- **NexusMods**: OAuth/SSO 登录、搜索浏览、一键下载、`nxm://` 协议、Collections 支持
- **CurseForge**: Mod/Modpack 搜索、直链下载
- **GitHub**: SMAPI 版本管理
- 登录失效智能提醒（可配置不再提示 / 本次不提示 / 去登录）
- API 请求频率限制与缓存管理

### 📋 Modpack 管理

- 创建自定义 Modpack（选择 Mod 打包导出为 `.zip`）
- 导入 SVL Modpack（`.zip` 格式，包含 `modpack.json`）
- CurseForge Modpack 格式兼容（`.zip` / `.cfmodpack`）
- Nexus Collection 格式兼容（`.7z` / 包含 `collection.json`）
- 拖放安装支持，可选安装目标路径

### ⬇️ 下载管理

- 内置多任务下载管理器（并发限制 3）
- 下载队列与进度追踪（速度 / ETA / 总大小 / 子进度）
- 多来源下载（NexusMods / CurseForge / 直链 / 浏览器引导）
- 下载悬浮球（支持拖动移动、红点提示）
- 任务状态实时日志面板

### 🎨 界面与主题

- 跨平台现代化 UI（Avalonia + Fluent 主题）
- 深色 / 浅色主题切换
- 全局主题配色系统（DynamicResource 资源体系）
- 可拖动悬浮球、居中加载动画
- 缓存管理（一键统计与清理）

## 快速开始

### 系统要求

- **操作系统**: Windows 10/11、Linux x64（建议 XDG 桌面环境）或 macOS 14+
- **运行时**: .NET 10 SDK（从源码构建时需要）
- **游戏**: Stardew Valley（Steam 或 GOG 版本）

### 安装

从 [Releases](../../releases) 页面下载最新版本：

发布页提供自带 .NET 运行环境的版本和名称包含 `requires_dotnet10` 的轻量版本。
轻量版需要先安装与系统架构一致的 [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；
如果不确定是否已安装，请选择自带环境的版本。

- **Windows**: 下载 `.zip`，解压后运行 `SVL.Avalonia.exe`
- **Ubuntu/Debian x64**: 下载 `.deb`，使用软件安装器打开，或运行 `sudo apt install ./svl-launcher_1.2.0.0_amd64.deb`；安装后可从应用菜单启动
- **Linux x64 免安装版**: 下载 ZIP，解压后运行 `sh run-svl.sh`
- **macOS**: 下载 `.dmg`，拖入 Applications 文件夹后运行

### 从源码构建

```bash
# 克隆仓库
git clone --branch main --single-branch https://github.com/panda-lsy/SVL-StardewValleyLauncher.git
cd SVL-StardewValleyLauncher

# Debug 构建
dotnet build SVL.Avalonia/SVL.Avalonia.csproj --configuration Debug

# Release 构建
dotnet build SVL.Avalonia/SVL.Avalonia.csproj --configuration Release

# 运行
dotnet run --project SVL.Avalonia/SVL.Avalonia.csproj
```

`SVL.sln` 包含 Avalonia 主项目、平台层与迁移测试。旧 WPF/Core 源码暂留仓库供迁移历史对照，但不属于新版默认解决方案或发布依赖。

### 打包发布

使用根目录的统一打包脚本发布 Windows/Linux/macOS Release 版本；需要排查问题时，可在应用设置中开启“调试模式”并打开调试窗口，无需单独下载 Debug 版：

```powershell
# Windows x64 Release
.\build.ps1 -Config Release -Targets windows

# 轻量版（各目标均支持，目标机器须预装对应架构的 .NET 10 Runtime）
.\build.ps1 -Config Release -Targets windows -FrameworkDependent
.\build.ps1 -Config Release -Targets linux -FrameworkDependent
.\build.ps1 -Config Release -Targets macos -FrameworkDependent

# 全部目标（Windows + Linux + macOS）双配置
.\build.ps1 -Config all -Targets all

# Linux x64
.\build.ps1 -Config Release -Targets linux

# 仅 macOS（需在 macOS 上运行以生成 .dmg）
.\build.ps1 -Config Release -Targets macos
```

产物输出到 `artifacts/` 目录，命名格式：`SVL_v1.2.0.0_{config}_{platform}_{arch}.{ext}`

macOS DMG 内含自定义 Finder 背景及可拖拽的 `Stardew Valley Launcher.app`、`Applications` 链接。
Finder 布局使用 `packaging/macos/installer-layout.dsstore`，打包时无需启动 Finder；
如需重新设计布局，可用 `scripts/generate-macos-dmg-layout.py` 重新生成该文件。
当前构建使用临时 ad-hoc 签名，正式分发仍需 Developer ID 签名和 Apple 公证。

SteamCMD 首次安装会按系统下载官方 Windows、macOS 或 Linux 安装包。Apple 芯片上的
macOS SteamCMD 官方启动包是 Intel 架构，需要 Rosetta 2。

各平台默认包自带运行时；轻量包名称带
`requires_dotnet10`，需要安装对应架构的 .NET 10 Runtime，不能作为旧版的直接更新文件。
自包含构建还生成 `SVL.Desktop_v{版本号}_Release.exe`：旧版更新器要求发布资产
是真正的单文件 EXE，不能将 ZIP 改名为 EXE。Actions 工件不会自动成为 Release 资产。
已有 Debug 预览版需要先手动切换到 Release，才能继续接收后续只提供 Release 的更新。

新版会显示当前包体类型并优先读取 Release 中的 `update.txt`；检查更新时只选择同类型
的安装包。Windows 支持应用内替换，macOS 和 Linux 当前需前往发布页手动安装。无法识别
包体或发布页没有同类型文件时，会提示手动选择，不会自动切换类型。

新版首次启动会读取旧版配置和实例列表，旧文件会保留；已有用户再次启动时会补迁移各实例
的独立启动参数，已经在新版单独保存的设置优先。原版游戏和 Mod 文件不会因配置迁移被移动。

预发布验收步骤与各平台下载链接模板见 [发布准备](docs/release/发布准备.md)。

Linux x64 ZIP 解压后可运行 `sh run-svl.sh`；脚本会设置 apphost 执行权限并转发参数。
在安装了 `dpkg-deb` 的 Linux 上打包时，还会生成 Ubuntu/Debian x64 `.deb` 并在应用菜单中添加 SVL。
轻量 DEB 需要软件源提供 `dotnet-runtime-10.0`，它与自带环境的 DEB 使用相同的软件包名，安装其中之一即可。
也可单独运行 `sh scripts/package-linux-deb.sh <Linux ZIP> <输出目录> 1.2.0.0`；
轻量 ZIP 还需在末尾加上 `framework-dependent`，输出目录中不能已有同名安装包。

## 项目结构

```
SVL/
├── SVL.Avalonia/             # Avalonia 跨平台桌面应用（主项目）
│   ├── ViewModels/           #   MVVM ViewModels
│   ├── Views/                #   AXAML 页面
│   ├── Controls/             #   自定义控件
│   ├── Resources/            #   主题与样式资源
│   ├── Services/             #   应用服务层
│   ├── Models/               #   数据模型
│   └── Assets/               #   图标与图片资源
├── SVL.Core.Platform/        # 平台抽象层（Windows/Linux/macOS 实现）
├── SVL.Core/                 # 旧 WPF/Core 源码（暂留参考，不在新版默认构建中）
├── SVL.Desktop/              # 旧 WPF UI 源码（暂留参考，不在新版默认构建中）
├── SVL.Migration.Tests/      # 迁移测试
├── build.ps1                 # 统一打包脚本
├── CHANGELOG.md
└── SVL.sln
```

## 技术栈

| 层级     | 技术                           |
| -------- | ------------------------------ |
| 运行时   | .NET 10                        |
| UI 框架  | Avalonia UI 11.2 + Fluent 主题 |
| MVVM     | CommunityToolkit.Mvvm 8.4      |
| 配置     | System.Text.Json               |
| 跨平台   | Windows / Linux / macOS        |
| 测试     | xUnit                          |

## 配置文件

| 文件           | 位置                                                | 格式 |
| -------------- | --------------------------------------------------- | ---- |
| 全局配置       | `%LocalAppData%\SVL\config.json`                   | JSON |
| 用户设置       | `%LocalAppData%\SVL\usersettings.json`             | JSON |
| 实例配置       | `%LocalAppData%\SVL\instances\{id}\instance.json`  | JSON |
| 任务状态       | `%LocalAppData%\SVL\downloadtasks.json`            | JSON |
| 社区汉化缓存   | `%LocalAppData%\SVL\community-i18n\`               | JSON |

## 贡献

欢迎提交 Issue 和 Pull Request！

1. Fork 本仓库
2. 创建功能分支 (`git checkout -b feature/my-feature`)
3. 提交更改 (`git commit -m 'feat: add my feature'`)
4. 推送分支 (`git push origin feature/my-feature`)
5. 发起 Pull Request

## 致谢

- [PCL2-CE](https://github.com/PCL-Community/PCL2-CE) — 架构设计参考
- [SMAPI](https://github.com/Pathoschild/SMAPI) — Stardew Valley Modding API
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM 框架
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) — 跨平台 UI 框架
- [NexusMods](https://www.nexusmods.com/stardewvalley) — Mod 资源平台
- [CurseForge](https://www.curseforge.com/stardewvalley) — Mod 资源平台
- [星露谷物语社区本地化](https://github.com/panda-lsy/StardewValley-Community-Localization) — 社区汉化数据

## 许可证

[Apache License 2.0](LICENSE)
