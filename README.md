<p align="center">
  <img src="SVL.Desktop/Images/icon.png" alt="SVL Logo" width="128" height="128">
</p>

<h1 align="center">Stardew Valley Launcher</h1>

<p align="center">
  <b>一站式星露谷物语启动器 · Mod 管理器 · Modpack 工具</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-purple?logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/Avalonia-11.2-blueviolet" alt="Avalonia">
  <img src="https://img.shields.io/badge/License-Apache_2.0-green" alt="License">
  <img src="https://img.shields.io/badge/Platform-Windows_/_macOS-0078D6" alt="Cross-Platform">
  <img src="https://img.shields.io/github/v/release/panda-lsy/SVL-StardewValleyLauncher?include_prereleases" alt="Release">
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

- **操作系统**: Windows 10/11 或 macOS 12+
- **运行时**: .NET 10 SDK（从源码构建时需要）
- **游戏**: Stardew Valley（Steam 或 GOG 版本）

### 安装

从 [Releases](../../releases) 页面下载最新版本：

- **Windows**: 下载 `.zip`，解压后运行 `SVL.Avalonia.exe`
- **macOS**: 下载 `.dmg`，拖入 Applications 文件夹后运行

### 从源码构建

```bash
# 克隆仓库
git clone https://github.com/panda-lsy/SVL.git
cd SVL

# Debug 构建
dotnet build SVL.sln --configuration Debug

# Release 构建
dotnet build SVL.sln --configuration Release

# 运行
dotnet run --project SVL.Avalonia
```

### 打包发布

使用根目录的统一打包脚本，支持 Windows/macOS 的 Debug/Release 配置：

```powershell
# Windows x64 Debug 单文件
.\build.ps1 -Config Debug -Targets windows

# Windows x64 Release
.\build.ps1 -Config Release -Targets windows

# 全部目标（Windows + macOS）双配置
.\build.ps1 -Config all -Targets all

# 仅 macOS（需在 macOS 上运行以生成 .dmg）
.\build.ps1 -Config Release -Targets macos
```

产物输出到 `artifacts/` 目录，命名格式：`SVL_v1.2.0.0_{config}_{platform}_{arch}.{ext}`

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
├── SVL.Core/                 # 核心功能库（平台无关）
│   ├── App/                  #   应用核心与生命周期
│   ├── Config/               #   配置系统
│   ├── Download/             #   下载管理器
│   ├── IO/                   #   文件服务
│   ├── Stardew/              #   星露谷核心
│   │   ├── Instance/         #     实例管理与隔离
│   │   ├── Launch/           #     游戏启动编排
│   │   ├── Mod/              #     Mod 管理与依赖解析
│   │   └── ResourceProject/  #     NexusMods / Modpack 集成
│   └── Utils/                #   工具类
├── SVL.Core.Platform/        # 平台抽象层（Windows/macOS 实现）
├── SVL.Desktop/              # 旧 WPF 架构（参考保留）
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
| 跨平台   | Windows / macOS                |
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
