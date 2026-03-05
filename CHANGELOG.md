# Changelog

All notable changes to SVL (Stardew Valley Launcher) will be documented in this file.

## [1.1.5.1] - 2026-03-05

### Added

- **整合包图标自动检测与保存** - 支持多种图标命名规范
  - 支持 `modpack-icon.*`、`icon.*`、`logo.*`、`thumbnail.*`、`cover.*`、`pack-icon.*` 等命名
  - 支持 PNG、JPG、JPEG、WEBP、GIF 等多种图片格式
  - 优先检测与整合包同名的 sidecar 图标文件（如 `MyModpack.png`）
  - 整合包安装时自动保存图标到版本隔离目录（`.svl-instance-icon.*`）
- **实例图标自动回退** - 从版本隔离目录加载自定义图标
  - 当实例启用隔离且未设置自定义图标时，自动从 `versions/{实例名}/` 目录加载 `.svl-instance-icon.*`
  - 支持在实例设置中查看和更换图标
- **实例名称和描述自动保存** - 防抖优化避免频繁写入
  - 实例名称和描述修改后 800ms 自动保存到配置文件
  - 名称合法性验证（禁止特殊字符、禁止以空格/点结尾）
  - 保存状态实时显示在右上角（✓ 已保存 / ✗ 保存失败）
- **构建时自动生成带版本号的 exe 副本**
  - Debug/Release 构建时自动复制 `SVL.Desktop_v{version}_{configuration}.exe`
  - 便于区分不同版本和构建类型

### Changed

- **导出策略调整** - 遵循来源平台分发规则
  - 移除「打包 Mod 文件」选项，不再导出 Mod 本体文件
  - 有来源凭证的 Mod：仅导出清单和来源信息（最低可迁移信息）
  - 无来源凭证的 Mod：导出配置文件（config.json 等）
  - 更新导出页面 UI 文案，清晰说明导出策略
  - 整合包导出时支持打包实例图标到压缩包根目录
- **实例刷新逻辑优化** - 保留用户元数据
  - 刷新实例列表时保留自定义图标、描述、收藏等用户设置
  - 仅更新检测字段（版本号、SMAPI 版本等）
  - 避免重复创建实例对象
- **全局实例变更监听** - 自动同步选中状态
  - 监听 `GlobalEvents.InstanceChanged` 事件
  - 实例配置变更后自动刷新左侧启动页的选中状态
  - 支持通过实例 ID 精准同步
- **未处理异常上下文记录** - 增强错误诊断能力
  - 记录当前页面类型、左右面板内容
  - 记录焦点元素和鼠标悬停元素
  - 记录完整的视觉树路径（最多 8 层）
- **UI 样式优化**
  - 整合包导入对话框支持图标预览（有图标显示图片，无图标显示默认图标）
  - 实例选择器样式优化，选中项使用主题色高亮显示
  - 滚动条样式统一，支持拖动状态高亮（主题色）
  - 下载页文本截断优化，避免溢出（TextTrimming + TextWrapping）
  - 版本设置页顶部显示当前实例名称和保存状态
- **依赖包升级** - System.Text.Json 升级到 6.0.11

### Fixed

- **实例图标路径验证** - 避免加载不存在的文件
  - 检查自定义图标路径是否为绝对路径
  - 检查文件是否存在，不存在则回退默认图标
- **转换器 UnsetValue 处理** - 避免绑定错误
  - BoolToVisibilityConverter 和 InverseBoolToVisibilityConverter 处理 UnsetValue
  - 防止绑定未完成时显示异常
- **滚动条模板引用错误** - 统一使用 DynamicResource
  - 修复 ModernStyles.xaml 中滚动条模板使用 StaticResource 导致的问题
  - 支持运行时主题切换

### Removed

- **Mod 文件打包选项** - 导出页面移除「打包 Mod 文件」复选框
  - 遵循 Mod 平台分发规则，尊重创作者权益
  - 简化导出流程，减少用户困惑

---

## [1.1.5] - 2026-03-03

### Added

- **游戏实例自动检测** - 首次启动时自动检测已安装的游戏
  - 支持 Steam 安装检测（读取注册表 + libraryfolders.vdf）
  - 支持 GOG Galaxy 安装检测
  - 支持 Xbox Game Pass 安装检测
  - 自动创建并保存检测到的实例
- **NXM 协议测试支持** - 支持 `nxm://test/link` 测试 URL，用于 Wiki 测试 NXM 协议联动
  - 收到测试 URL 时显示成功通知

### Changed

- **主页面优化** - 默认实例名称改为"未找到可用的游戏"，更清晰地提示用户
- **依赖包精简** - 移除 6 个未使用的 NuGet 包，减少程序体积
  - 移除: System.Management, Ae.Dns.Client, Humanizer.Core.zh-CN, LiteDB, Microsoft.Extensions.Http, Polly
  - 保留: SharpCompress, SharpZipLib, YamlDotNet, System.Text.Json, CommunityToolkit.Mvvm

### Removed

- **下载页实用工具分类** - 简化下载页面布局

### Fixed

- **HashCode.Combine 兼容性** - 修复 .NET Framework 4.8 下 HashCode.Combine 不可用的问题

---

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
