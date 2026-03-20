<p align="center">
  <img src="SVL.Desktop/Images/icon.png" alt="SVL Logo" width="128" height="128">
</p>

<h1 align="center">Stardew Valley Launcher</h1>

<p align="center">
  <b>一站式星露谷物语启动器 · Mod 管理器 · Modpack 工具</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET_Framework-4.8-purple?logo=dotnet" alt=".NET 4.8">
  <img src="https://img.shields.io/badge/WPF-Desktop-blue?logo=windows" alt="WPF">
  <img src="https://img.shields.io/badge/License-Apache_2.0-green" alt="License">
  <img src="https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows" alt="Windows">
  <img src="https://img.shields.io/github/v/release/panda-lsy/SVL-StardewValleyLauncher?include_prereleases" alt="Release">
  <a href="https://svl.qzz.io" target="_blank">
    <img src="https://img.shields.io/badge/官网-SVL%20Website-2F855A?style=flat-square" alt="Official Website">
  </a>
  <a href="https://svl.qzz.io/contribute.html" target="_blank">
    <img src="https://img.shields.io/badge/社区本地化-参与贡献-0F766E?style=flat-square" alt="Community Localization Contribution">
  </a>
  <a href="https://wiki.svl.qzz.io/" target="_blank">
    <img src="https://img.shields.io/badge/📖_查看_Wiki-使用教程-4A90D9?style=flat-square" alt="View Wiki">
  </a>
  <a href="https://github.com/panda-lsy/SVL-Wiki/tree/main" target="_blank">
    <img src="https://img.shields.io/badge/✏️_参与编写-Wiki贡献-28A745?style=flat-square" alt="Contribute Wiki">
  </a>
  <a href="https://ifdian.net/a/mcshengxia" target="_blank">
    <img src="https://img.shields.io/badge/爱发电-赞助支持-FF6B6B?logo=love&logoColor=white" alt="Sponsor">
  </a>
</p>

<p align="center">
  <img src="https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.svl.qzz.io%2Fapi%2Fstats%2Fbadges%2Fusers-total.json&style=flat" alt="SVL 用户总量">
  <img src="https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.svl.qzz.io%2Fapi%2Fstats%2Fbadges%2Fusers-daily.json&style=flat" alt="SVL 近24小时活跃用户">
</p>

<p align="center">
  <img src="https://api.svl.qzz.io/api/stats/charts/users-total-trend.svg" alt="SVL 总用户数量" width="760">
</p>

---

## 关于

**SVL (Stardew Valley Launcher)** 是一个功能完整的星露谷物语启动器和 Mod 管理器。它提供现代化的 Windows 桌面体验，让你轻松管理游戏实例、安装和组织 Mod、集成 NexusMods 下载，以及创建和分享 Modpack。

## 功能亮点

### 🎮 游戏启动

- 自动检测 Stardew Valley 安装路径（Steam / GOG）
- 多游戏实例管理与隔离
- SMAPI 自动检测、安装与版本管理
- 支持原版与 Mod 模式启动
- 自定义窗口标题与启动参数

### 📦 Mod 管理

- 从 ZIP 文件或文件夹安装 Mod
- 一键启用 / 禁用 / 卸载
- 智能依赖解析（拓扑排序 + 循环检测）
- 冲突检测（ID 重复、文件冲突、依赖冲突）
- Mod 详情查看（版本、作者、描述、依赖关系）

### 🌐 NexusMods 集成

- NexusMods 搜索与浏览
- OAuth / SSO 登录认证
- 一键下载并安装 Mod
- `nxm://` 协议支持（浏览器一键安装）
- NexusMods Collections 支持
- API 请求频率限制与缓存管理

### 📋 Modpack 管理

- 创建自定义 Modpack（选择 Mod 打包导出为 `.zip`）
- 导入 SVL Modpack（`.zip` 格式，包含 `modpack.json`）
- CurseForge Modpack 格式兼容（`.zip` / `.cfmodpack`）
- Nexus Collection 格式兼容（`.7z`/包含  `collection.json`）
- 拖放安装支持

### ⬇️ 下载管理

- 内置多任务下载管理器
- 断点续传支持
- 多来源下载（NexusMods / CurseForge / 直链）
- 下载队列与进度追踪

### 🎨 界面与主题

- Windows 11 风格现代化 UI
- 深色 / 浅色主题切换
- 可自定义主题配色
- 响应式布局设计

## 快速开始

### 系统要求

- **操作系统**: Windows 10 / 11
- **运行时**: .NET Framework 4.8（Windows 10+ 已内置）
- **游戏**: Stardew Valley（Steam 或 GOG 版本）

### 安装

从 [Releases](../../releases) 页面下载最新版本，解压后运行 `SVL.Desktop.exe` 即可。

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
dotnet run --project SVL.Desktop
```

> 构建需要 .NET SDK（支持 .NET Framework 4.8 目标）以及 Windows 环境。

## 项目结构

```
SVL/
├── SVL.Core/                 # 核心功能库
│   ├── App/                  #   应用核心与生命周期管理
│   ├── Config/               #   配置系统
│   ├── Download/             #   下载管理器
│   ├── IO/                   #   文件服务
│   ├── Logging/              #   日志系统
│   ├── Modpack/              #   Modpack 格式解析
│   ├── Security/             #   安全工具
│   ├── Stardew/              #   星露谷核心
│   │   ├── Instance/         #     实例管理与隔离
│   │   ├── Launch/           #     游戏启动编排
│   │   ├── Mod/              #     Mod 管理与依赖解析
│   │   └── ResourceProject/  #     NexusMods / Modpack 集成
│   └── Utils/                #   工具类
├── SVL.Desktop/              # WPF 桌面应用
│   ├── ViewModels/           #   MVVM ViewModels
│   ├── Views/                #   XAML 页面
│   ├── Controls/             #   自定义控件
│   ├── Resources/            #   主题与样式资源
│   └── Images/               #   图标与图片
├── SVL.Tests/                # 单元测试
└── SVL.sln
```

## 技术栈

| 层级     | 技术                                  |
| -------- | ------------------------------------- |
| 运行时   | .NET Framework 4.8                    |
| UI 框架  | WPF (Windows Presentation Foundation) |
| MVVM     | CommunityToolkit.Mvvm                 |
| 配置     | YamlDotNet / System.Text.Json         |
| 压缩     | SharpZipLib / SharpCompress           |
| 嵌入依赖 | Costura.Fody                          |

## 配置文件

| 文件           | 位置                                                | 格式 |
| -------------- | --------------------------------------------------- | ---- |
| 全局配置       | `%LocalAppData%\SVL\config.yaml`                  | YAML |
| 实例配置       | `%LocalAppData%\SVL\instances\{id}\instance.json` | JSON |
| NexusMods 缓存 | `%LocalAppData%\SVL\cache\nexusmods\`             | -    |

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
- [NexusMods](https://www.nexusmods.com/stardewvalley) — Mod 资源平台
- [Vortex](https://github.com/Nexus-Mods/Vortex) — Mod 管理器设计参考
- [Mod Organizer 2](https://github.com/ModOrganizer2/modorganizer) — Mod 管理器设计参考
- [Stardrop](https://github.com/floogen/stardrop) — 星露谷 Mod 管理与依赖交互设计参考
- [CurseForge](https://www.curseforge.com/stardewvalley) — Mod 资源平台

## 许可证

[Apache License 2.0](LICENSE)
