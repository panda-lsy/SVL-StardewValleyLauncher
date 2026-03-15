# Changelog

All notable changes to SVL (Stardew Valley Launcher) will be documented in this file.

## [1.1.8.1] - 2026-03-15

### Added

- **Steam 启动参数覆写：**

  - 在版本设置-设置中可以直接覆写 Steam 一键启动参数为选中的实例，便于一键加入多人游戏
- **批量更新与下载优化**

  - 批量更新失败时继续处理下一个 Mod，不再中断整个流程
  - 下载失败时显示具体错误原因（403/404/超时等），替代模糊的"已取消"提示
  - 下载管理器失败时自动切换到对应任务的状态页，显示正确的错误详情
  - 为 CurseForge 添加新的下载源
- **SMAPI 版本切换增强**

  - 原版 Base 下若检测到 SMAPI 安装，则在设置添加一键切换到 SMAPI 版本的按钮
  - 禁止对 Base 原版游戏进行删除
  - 切换后自动刷新并选中 SMAPI 实例，无需手动切换页面
  - Base 实例安装 SMAPI 后正确显示 Modded 图标
  - 版本设置页面显示当前 SMAPI 版本信息
  - 使 Base 下的 SMAPI 可以被自动安装（切换版本）
- **Mod 管理性能优化**

  - 优化全选逻辑，按钮用于全选该分类下的全部 Mods，Mod 列表新增用于选择整页的全选按钮
  - 更新检测支持并发线程控制（设置中可配置 1-16 线程）
  - 汉化检测支持并发线程控制（设置中可配置 1-16 线程）
  - 汉化检测 & 更新检测进度实时显示
- **实例选择器改进**

  - 自动同步 Base 实例的 SMAPI 安装状态和版本信息
  - 为 SMAPI 实例添加了 Base Tag
  - 进入版本选择时优先选中当前实例所在路径分组
  - 刷新时合并检测到的 Base 实例与保存的实例信息

### Fixed

- 修复 Base SMAPI 版本更新后检测失败的问题
- 修复 Base SMAPI 进行自动安装时会污染游戏库文件的问题
- 修复批量更新单个 Mod 失败导致整个任务中断的问题
- 修复下载失败时状态页显示 SMAPI 安装而非实际 Mod 任务的问题
- 修复 Base 切换为 SMAPI 后主页仍选中原版的问题
- 修复版本设置页面中 Base SMAPI 实例显示 Vanilla 图标的问题
- 修复全选功能在分页模式下仅选中当前页
- 采用更合适的 Mod 下载接管方式，降低被下载器协议劫持的概率
- 修复了 Base 游戏版本下若无 /versions 文件夹拖入安装 SMAPI 自动选择其他游戏路径的问题

## [1.1.8.0] - 2026-03-13

### Added

- 社区本地化能力增强

  - 新增社区本地化服务与缓存层，支持 Gitee/GitHub 源选择
  - 新增 UniqueID 路径回退（Mods/UniqueID/{UniqueID}.json）
  - 检测汉化支持自动应用并给出汇总结果弹窗
- 本地/在线详情本地化交互增强

  - 新增贡献本地化入口与贡献者信息提示、本地化说明
  - 详情页支持中/英切换显示
  - 详情页支持跳转到本地化贡献页时一键填写资源信息
  - Mod 管理页面添加"检测汉化"按钮以一键扫描 Mods 列表的可更新的汉化内容并替换；
  - Mod 管理页面在动画按钮行与交互按钮行添加了汉化与显示语言切换按钮；
  - 本地信息页面添加了贡献本地化及语言切换按钮
- 在 设置-基本设置 添加了"打开缓存文件夹"按钮

### Changed

* 资源下载页面增强
  * 合并复制名称、复制 ID 为组合按钮
  * 修改 NexusMods 的 Collection 资源为复制尾链

- 搜索与展示体验优化
  - Mod/Modpack 列表增加本地化显示应用
  - 下载页与搜索页在多处优化缓存复用与首屏加载路径
  - 优化了 Mod 管理页面的 Mod 标题-描述 显示效果，与动画按钮行不再冲突
- 移除了启动器对 Curseforge API Key 的硬性要求
- 删除了 Mod 管理页面按钮行的图标
- 资源详情页面自动展开所有受支持游戏版本下的 Mod 版本列表改为只自动展开最新游戏版本下所支持的 Mod 版本列表

### Fixed

- 修复本地 Mod 详情贡献信息读取与显示链路
- 修复部分来源不可达时的降级行为与状态显示一致性
- Mod 管理页面缩略符 `...` 对夜间模式的支持

## [1.1.7.0] - 2026-03-09

### Added

- **拖放安装工作流**
  - 支持拖放整合包直接进入整合包安装流程
  - 支持拖放本地 Mod 压缩包，并在实例页或模组管理页直接安装到目标实例
  - 支持识别本地 SMAPI 安装包并引导创建新实例
- **Mod 管理前置依赖支持**
  - 本地 Mod 详情与在线详情页新增前置 Mod 列表、跳转与展开收起
  - 启用带前置的 Mod 时，支持按目标 Mod 分组勾选要一并启用的前置 Mod
- **父子 Mod 组合支持**
  - 基于 `svl-source.json` 建立父/子 Mod 层级与来源回溯
  - 管理页支持父子行展示、展开收起、联动选择与去重批量操作
- **Mod 管理体验增强**
  - 版本设置 -> Mod 管理新增从本地安装 Mod 的 `+` 按钮
  - Mod 管理增加启用 / 禁用分类筛选
  - Mod 行增加整行选择、悬停操作区与更清晰的本地详情入口
- **Mod 下载页增强**
  - Curseforge 搜索支持按游戏版本筛选
  - 支持在设置页控制“来源为全部时类型筛选不可用”的提示
  - 复制 Mod 名称或 ID 时显示成功提示

### Changed

- **搜索与导航体验优化**
  - 搜索框改为点击搜索按钮后再执行请求，不再边输入边自动搜索
  - 从 Mod 详情页返回搜索页时保留搜索条件、滚动位置和详情返回栈
  - 优化 NexusMods 按 ID 搜索与双来源分类搜索逻辑
- **Mod 显示与备份策略优化**
  - Mod 管理显示名称改为“文件夹名 | json 解析名 | 版本号 | 作者”
  - 备份目录默认命名改为“文件夹名-日期-副本X”
  - 父 Mod 备份与恢复会保留组合层级元数据
- **路径与输入体验优化**
  - Base 路径确认改为下拉选择所有已导入启动器的路径
  - 优化实例名称输入时的非法字符清理与光标位置保持
- **下载页版本展示优化**
  - Mod 详情页版本分组默认展开
  - 搜索卡片中的版本标签仅在需要的来源显示

### Fixed

- **Curseforge 模组更新修复**
  - 批量更新支持 Curseforge CDN 直链下载，不再错误要求手动打开浏览器
  - 无法从 `UpdateUrl` 提取 `fileId` 时改为直接沿用下载链接继续更新
- **Mod 更新就地替换修复**
  - 更新已重命名文件夹的 Mod 时，改为备份旧内容、保留配置并回填到原目录
  - 避免更新后旧目录与新目录并存
- **整合包与压缩包识别修复**
  - 提高 Curseforge manifest 识别准确度，避免普通 Mod 被误判为整合包
  - 修复 ZIP 解压时的目录创建与路径规范化问题
- **界面细节修复**
  - 修复实例路径选择对话框确认按钮状态与校验反馈
  - 修复主界面滚轮/滚动状态保留问题
  - 修复 Mod 管理选中圆点、行悬停高亮与操作按钮显隐

## [1.1.6.0] - 2026-03-06

### Added

- **MOD 备份管理系统** - 完整的 MOD 备份与恢复功能
  - 新增 `ModBackupService` 服务，支持将 MOD 移动到回收站而非直接删除
  - 使用 Windows API `SHFileOperation` 实现安全的文件删除（支持回收站恢复）
  - 备份元数据管理（`.svl-backup.json`），记录原始路径、备份时间、MOD 信息
  - 支持备份与活跃 MOD 一键互换（`SwapBackupWithActive`）
  - MOD 列表显示备份标签（💾）和备份时间
- **MOD 列表分页功能** - 优化大量 MOD 的展示性能
  - 每页显示 10 个 MOD，支持页码导航
  - 显示总页数和当前页码
  - 支持上一页、下一页、跳转到指定页
  - 页码按钮动态生成，最多显示 7 个页码（含省略号）
- **MOD 标签支持** - 增强 MOD 分类与管理
  - `SdVMod` 类新增 `Tags` 属性（`List<string>`）
  - 自动从 `manifest.json` 加载标签
  - UI 显示标签 badges（逗号分隔）
  - 支持通过标签快速识别 MOD 类型
- **MOD 嵌套目录支持** - 尊重 Mod 作者的目录结构
  - `ModManager` 递归扫描 `Mods` 目录及其子目录
  - 自动发现嵌套的 `manifest.json` 文件
  - 保留原始目录结构，不强制展开到根目录
  - 使用 `GetRelativePathPortable` 计算跨平台相对路径

### Changed

- **版本检测增强** - 3 级回退策略确保版本准确性
  - 优先使用 Entry Assembly 获取版本号
  - 回退到 SVL.Desktop Assembly
  - 最终回退到当前 Assembly
  - 增强日志记录，便于诊断版本比较问题
- **MOD 删除流程优化** - 使用回收站替代永久删除
  - `ModDownloadTask` 更新 MOD 时，旧版本移动到回收站
  - `DownloadRightViewModel` 使用 `ModBackupService.MovePathToRecycleBin`
  - 用户可从回收站恢复误删的 MOD
- **MOD 列表 UI 重构** - 更直观的备份管理体验
  - 新增"备份"筛选分类（全部 / 可更新 / 备份）
  - 上下文感知操作按钮（备份图标 / 恢复图标）
  - 选中项操作：备份选中、恢复选中备份
  - 响应式动作栏，支持横向滚动
- **实例名称保存逻辑优化** - 增强数据一致性
  - 添加 `IsDuplicateInstanceName` 验证，避免重名
  - 实例隔离目录重命名失败时回滚 UI 显示
  - 详细的错误提示（名称已存在/重命名失败）

### Fixed

- **下拉框遮挡问题** - 修复 MOD 详情页面 ComboBox 被遮挡
  - 调整 `ModDetailsView` 布局，增加底部间距
  - 优化滚动区域高度计算
- **ComboBox 样式统一** - 现代化下拉框样式
  - 统一使用 `ModernComboBox` 样式
  - 优化下拉箭头图标和动画
  - 支持主题色高亮

### Technical Details

- **新增文件**:
  - `SVL.Core/Stardew/Mod/ModBackupService.cs` (425 行)
- **修改文件**:
  - `SVL.Core/App/LauncherUpdateService.cs` - 版本检测逻辑
  - `SVL.Core/Download/ModDownloadTask.cs` - 备份集成
  - `SVL.Core/Stardew/Mod/ModManager.cs` - 递归扫描
  - `SVL.Core/Stardew/Mod/SdVMod.cs` - 标签与备份属性
  - `SVL.Desktop/ViewModels/VersionSettingsViewModel.cs` - 分页与备份命令
  - `SVL.Desktop/Views/VersionSettingsContentView.xaml` - 分页 UI
  - `SVL.Desktop/Views/VersionSettingsPageView.xaml` - 响应式动作栏

---

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
