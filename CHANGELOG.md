# Changelog

All notable changes to SVL (Stardew Valley Launcher) will be documented in this file.

## [1.1.0] - 2026-03-01

### Added

#### 关于页面（设置 → 关于）

- **作者 Card** - 展示作者信息（盛夏de背影 / mc_shengxia）
  - 左侧头像图标 `author-icon.png`
  - 爱发电赞助按钮 [`https://ifdian.net/a/mcshengxia`](https://ifdian.net/a/mcshengxia)
- **启动器 SVL Card** - 展示启动器信息
  - Junimo 图标 `Junimo.png`
  - 显示当前版本号
  - 查看源码按钮 [`https://github.com/panda-lsy/SVL-StardewValleyLauncher`](https://github.com/panda-lsy/SVL-StardewValleyLauncher)
- **检查更新按钮** - 手动检查启动器更新

#### 启动器自动更新功能

- **多源支持**
  - GitHub 源：`https://github.com/panda-lsy/SVL-StardewValleyLauncher`
  - Gitee 源：`https://gitee.com/mc_shengxia/SVL-StardewValleyLauncher`
- **智能版本匹配**
  - Release 版本自动更新 Release 版本
  - Debug 版本自动更新 Debug 版本（通过文件名区分）
- **更新提示** - 有新版本时自动显示提示
- **自动下载** - 支持自动下载更新包

### Changed

#### 文档更新

- **README.md** - 修正整合包格式说明
  - SVL Modpack：`.zip` 格式，包含 `modpack.json`
  - CurseForge Modpack：`.zip` / `.cfmodpack` 格式，包含 `manifest.json`
  - Nexus Collection：`.7z` 格式，包含 `collection.json`
- **README.md** - 添加爱发电赞助 Badge

---

## [1.0.0] - Initial Release

### Added

- 游戏启动功能

  - 自动检测 Stardew Valley 安装路径（Steam / GOG）
  - 多游戏实例管理与隔离
  - SMAPI 自动检测、安装与版本管理
  - 支持原版与 Mod 模式启动
- Mod 管理

  - 从 ZIP 文件或文件夹安装 Mod
  - 一键启用 / 禁用 / 卸载
  - 智能依赖解析（拓扑排序 + 循环检测）
  - 冲突检测（ID 重复、文件冲突、依赖冲突）
- NexusMods 集成

  - NexusMods 搜索与浏览
  - OAuth / SSO 登录认证
  - 一键下载并安装 Mod
  - `nxm://` 协议支持
- Modpack 管理

  - 创建自定义 Modpack
  - 导入 SVL Modpack
  - CurseForge Modpack 格式兼容
- 下载管理

  - 内置多任务下载管理器
  - 断点续传支持
  - 多来源下载
- 界面与主题

  - Windows 11 风格现代化 UI
  - 深色 / 浅色主题切换
  - 可自定义主题配色
