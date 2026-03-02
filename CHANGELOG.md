# Changelog

All notable changes to SVL (Stardew Valley Launcher) will be documented in this file.

## [1.1.4] - 2026-03-02

### Added

- **自定义更新完成对话框** - 全新的更新完成提示界面
  - 显示更新成功的图标和动画效果
  - 标题动态显示当前版本号
  - 滚动显示完整更新日志
  - 使用项目统一的自定义滚动条样式
  - 在主窗口完全加载后显示，避免 UI 闪烁

### Changed

- **更新系统优化**
  - 更新完成对话框从服务器获取实际的 Release Notes
  - 优化对话框显示时机，等待主窗口加载完成

### Fixed

- **Prerelease 版本检测修复** - 修复切换 prerelease 设置后缓存未正确失效的问题
- **Prerelease 设置持久化** - 修复 prerelease 设置重启后丢失的问题
- **更新重启程序启动失败** - 修复 batch 脚本中 `start` 命令参数解析错误，改用 PowerShell `Start-Process`

---

## [1.1.3.1] - 2026-03-02

### Fixed

- **Prerelease 版本检测修复** - 修复切换 prerelease 设置后缓存未正确失效的问题
- **Prerelease 设置持久化** - 修复 prerelease 设置重启后丢失的问题
- **更新重启程序启动失败** - 修复 batch 脚本中 `start` 命令参数解析错误，改用 PowerShell `Start-Process`

### Changed

- **更新完成对话框优化**
  - 自定义对话框 UI，替代系统 MessageBox
  - 标题显示实际版本号（如"已更新到 v1.1.3 版本"）
  - 优先从服务器获取实际 Release Notes
  - 使用项目统一的自定义滚动条样式
  - 在主窗口完全加载后显示，避免闪烁

---

## [1.1.3] - 2026-03-02

### Added

- **Gitee 更新源支持** - 国内用户可使用 Gitee 源加速更新
- **更新源切换** - 设置页面支持 GitHub / Gitee 更新源切换
- **Prerelease 版本检测** - 支持检测预发布版本（Debug 构建默认启用）
- **Update.txt 支持** - 支持 Release 中包含 Update.txt 作为更新日志

### Changed

- **更新系统重构**
  - 分离 GitHub / Gitee 缓存，避免切换源时缓存混乱
  - 优化版本比较逻辑，支持 prerelease 版本
  - 增强错误处理和日志记录

### Fixed

- **更新对话框样式优化** - 更新日志区域使用自定义滚动条
- **更新失败提示** - 更新失败时弹窗显示详细错误信息

---

## [1.1.2] - 2026-03-01

### Added

- **启动器自动更新功能完整实现**
  - 更新检查对话框 (`UpdateDialog`)
  - 自动下载更新包
  - 一键安装更新（解压 + 替换 + 重启）
- **更新设置**
  - 启动时自动检查更新开关
  - 跳过此版本功能
- **更新进度显示** - 下载进度、解压进度实时显示

### Changed

- **设置页面布局优化** - 更新设置区域重新设计

---

## [1.1.1] - 2026-03-01

### Fixed

- **设置左侧栏主题响应** - 切换主题时高亮颜色现在会实时更新
- **关于页面自动检查更新** - 切换到"关于"选项卡时自动检查更新，无需手动点击

---

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
