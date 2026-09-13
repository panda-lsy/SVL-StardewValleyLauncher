# SVL Avalonia 迁移审计

更新时间：2026-09-13

本报告以当前工作树为准，重点覆盖 Avalonia 主流程与 WPF 旧配置兼容，不把
`SVL.sln` 中与 Avalonia 无关的旧 WPF/SMAPI 编译问题误计入迁移结果。

## `upstream/main` 分支对照结论

对照基准为当前 `Avalonia-Dev` 的 `HEAD`（`44a21b2`）、远程
`upstream/main`（`19ef4ef`）以及两者共同祖先（`7e92bdc`）。共同祖先之后，
`upstream/main` 只有一个 README 说明性提交，没有新增 WPF/Core 业务代码；
`main` 仍是 .NET Framework 4.8 + WPF 旧架构，且不包含 `SVL.Avalonia`。
因此，直接用 `git diff HEAD..upstream/main` 会把 Avalonia 工程显示成“被删除”，
这表示分支历史已经分叉，不表示 main 存在一批尚未移植的新增功能。

旧 WPF 页面和 Avalonia 页面已经按功能合并迁移。WPF 的本地 Modpack 管理页在
`upstream/main` 中本身仍是“功能开发中”占位页；Avalonia 已将可用的导入入口放在
实例页、导出入口放在版本设置页，并将在线搜索/详情放在下载流程中，不能把这个
占位页当成遗漏的成熟功能。

目前确认的真实缺口/边界如下：

| 项目 | 对照结论 | 处理计划 |
| --- | --- | --- |
| `manifest.json` 的 `UpdateKeys=GitHub:...` 自动检查更新 | WPF/Core 的 `ModManager` 仍明确记录为 TODO；Avalonia 已补齐 GitHub 仓库/release 解析、稳定版本比较、Release 压缩包选择、任务状态/导出/整合包来源持久化，并覆盖回归测试 | 后续只需在真实 GitHub 仓库做一次端到端 UI 验收；无 Release 压缩包时会明确提示，不把源码包误当 Mod 安装包 |
| Nexus Collection 的 `manual` 来源 | WPF 安装器仍是 TODO；Avalonia 已覆盖 NXM、API、浏览器回调及 HTTP 直链，并将 `manual` 网页/无来源条目标记为需手动处理；只有明确归档直链才自动下载 | 任务页保留来源地址并提供“打开来源”，用户完成下载后可拖入当前实例 Mods 页面；不把网页地址当压缩包 |
| Nexus Collection 非 Premium 逐项向导 | WPF 的 `NexusCollectionWizardTask` 还提供阶段分页、上一页/下一页和手动跳过可选 Mod；Avalonia 已改为统一安装队列，任务详情已展示当前 Mod、阶段、可选/必需状态、逐项结果，并支持打开来源或选择本地归档恢复；仍未提供旧向导的人工逐页导航/手动跳过交互 | 保持统一队列作为默认路径；若真实用户仍需要逐页决策，再在任务详情增加“不安装此可选项/继续下一项”，并复用现有来源凭证、备份和状态模型，不恢复旧任务类型 |
| 启动器“发现新版本后自动下载”设置 | WPF 只保存 `AutoDownloadUpdate` 字段，代码未发现实际消费逻辑；Avalonia 目前实现的是启动时自动检查和用户确认后下载安装 | 暂不视为已存在但漏迁移的完成特性；若确定需要，再单独定义自动下载/通知策略 |
| 本地 Modpack 管理列表 | `main` 的 `ModpacksLeft/RightViewModel` 仍只显示“开发中”；不是可迁移的完整功能 | 继续使用 Avalonia 的实例导入、版本设置导出和在线 Modpack 页面；未来若需要再设计独立列表 |
| WPF 专属实现细节 | WPF 的 `ImageCacheService`、`SearchCacheService`、下载任务类、NXM 注册、实例/启动服务等已由 Avalonia 服务或 `SVL.Core.Platform` 重构承接，类名不同不代表缺失 | 以行为验收和回归测试为准，不按一一同名复制 |
| 完整线上/可见 UI 验收 | 不是分支迁移代码缺口；当前主要剩真实 Nexus/CurseForge 网络和 Windows Avalonia 窗口冒烟 | 作为发布前验收项继续执行 |
| 旧 WPF .NET Framework 4.8 编译 | 属于旧工程本身的 Developer Pack/环境债务，不影响 Avalonia 工程 | 单独准备 .NET Framework 4.8 构建环境，不回填 Avalonia |

## 已完成并已验证

| 功能 | 当前状态 | 主要实现位置 |
| --- | --- | --- |
| 主壳、导航、启动页、实例页、版本设置、下载页、任务页、设置页 | 已迁移 | `SVL.Avalonia/MainWindow.axaml`、`ViewModels/*` |
| 本地/在线 Modpack/Collection 导入任务 | 已接入统一队列并自动启动；在线整合包下载完成后会进入对应整合包安装器，不再误走普通 Mod 安装；手动 Modpack URL 会在下载后识别 SVL/CurseForge/Collection 类型并转入对应安装器 | `MainWindowViewModel`、`DownloadPageViewModel` |
| ZIP、CFModpack、7z 识别与解压 | 已覆盖检测、普通 Mod、Collection、整合包本体和嵌套 `modpack.7z`；无扩展名下载也按 ZIP/7z 文件签名识别，普通 Mod 安装与整合包入口共用 ZIP/7z 判断；Collection 外层目录下的 `collection.json/bundled` 会按实际清单根解析；Collection source 支持旧字符串/URL 形态；外层同名但非目标格式的 JSON 不会遮蔽内层有效清单；CurseForge 允许 `files: []/null` 的 overrides-only 包继续进入安装阶段 | `SVL.Core.Platform/IO/ArchiveExtractor.cs`、`ModpackTypeDetector`、`ModpackInstallService`、`CollectionInstallService` |
| 历史来源标识兼容 | 可从 `cf-项目ID-文件ID` 恢复 CurseForge 来源；可从 Nexus `File 文件ID_...` 文件名补回 FileID；仅有 CurseForge projectId 时可从 CDN 路径回填 FileID（Modpack/Collection 共用），减少整合包导入/再次导出时丢失来源；Collection 的 CurseForge 直链会明确写入 CurseForge 而不是误记为 Nexus | `ModpackInstallService.TryGetModSourceDescriptor`、`CollectionInstallService` |
| SMAPI 安装包复用 | 精确缓存 → CurseForge manifest 的 ProjectID/FileID 缓存/直链 → Nexus ModID/FileID 缓存 → 兼容缓存 → 已有实例 → 远程目录；目标路径会归一化到所属 Base；无版本文件名的 Nexus 缓存会读取压缩包内部 SMAPI 目录并按目标版本过滤；目标版本明确但候选版本未知时拒绝复用；远程目录为空或不含兼容大版本时按清单版本回退官方 Release，并继续校验 `install.dat`；版本选择器也会保留 CurseForge FileID，版本设置页创建的 SMAPI 任务会持久化平台/项目/FileID，任务恢复时按 SMAPI 归档结构校验 Nexus/CurseForge 缓存；并行导入时 Modpack/Collection 对全局 SMAPI 临时缓存按版本共享解析锁，避免不同 Base 的临时包互相覆盖；GitHub 发布同时提供单层/双层 ZIP 时优先使用单层 `-installer.zip`，历史缓存、Nexus 回调和直链返回双层包时也会先解包 | `SmapiDownloadService`、`RemoteCatalogService`、`ModpackInstallService`、`CollectionInstallService`、`SmapiPackageVersionInspector`、`InstalledSmapiPackageBuilder`、`InstanceRuntimePathResolver` |
| Nexus NXM/网页文件地址 | 支持 API、浏览器回调、网页 URL 的 Mod/File ID 解析；Collection 镜像只择一下载并交给 `collection.json` 安装器；只有 ModID 的旧来源可优先复用任意有效缓存并恢复 FileID；空字符串字段和页面查询参数也会恢复 ID；SMAPI NXM 等待按 FileID 隔离，重复回调只认领一次，迟到回调不会覆盖其他安装任务；详情页已有真实 Nexus CDN/归档直链时直接使用，Nexus 文件页仍进入 NXM 浏览器回退；导入清单若保存了带 `key/expires/user_id` 的 NXM 链接，会保留一次性凭据，不再因重建无凭据链接而重复要求打开浏览器；即使清单中的 CDN 直链已过期，只要 ModID/FileID 稳定且本地已有有效缓存，也会先从缓存安装并补写来源凭证；Collection 安装也复用该凭据保留逻辑 | `NexusModDownloadResolverService`、`NexusDownloadCache`、`SmapiDownloadService`、`ModpackInstallService`、`CollectionInstallService` |
| 浏览器回退并发 | 同一 Mod/File 或 Collection 重复入队时，旧等待者只清理自身，不会误删新等待者；重复等待和回调匹配已有自动化回归 | `BrowserDownloadFallbackService` |
| CurseForge 文件下载 | 支持嵌套对象/字符串 `downloadUrl` 响应、大小写差异、有限重试和安装后校验；含 project/file ID 时不再把 CurseForge 网页当压缩包，优先解析 CDN；下载页、批量更新、Modpack/Collection 都会按稳定的 ProjectID/FileID 复用有效归档，CDN 签名变化或重启后无需重复下载；CurseForge 整合包本体任务也会持久化 ProjectID/FileID，缓存未命中且 CDN 失效时自动刷新地址；无 ID 的网页会明确失败；导入 CurseForge manifest 时会跳过 SMAPI 条目和 `required:false` 可选 Mod，避免将无需安装的条目误报为失败 | `RemoteCatalogService`、`CurseforgeDownloadCache`、`ModpackInstallService`、`CollectionInstallService`、`DownloadPageViewModel` |
| 在线搜索分页 | Mod/Modpack 页面统一使用服务端分页结果；多来源交错合并时按全局结果分页，避免翻页跳过另一来源条目；按钮可用状态随页码更新；兼容入口补齐游戏版本/类型筛选、重置条件和配置默认来源 | `RemoteCatalogService`、`FeaturePagesViewModels`、`Views/ModSearchPageView.axaml`、`Views/ModpackSearchPageView.axaml` |
| 详情下载项校验 | 说明文本（暂无文件、登录提示、NXM 指引）不再显示为可安装版本；保留 Nexus/CurseForge 仅有 FileID、由解析器获取直链的合法项 | `FeaturePagesViewModels`、`ModDetailsPageView` |
| 已安装 Mod 更新确认 | 详情页点击“升级/回退/重新安装”前会读取当前实例的已安装版本，展示当前/目标版本并要求确认；取消不会创建下载任务 | `FeaturePagesViewModels`、`DialogService`、`Controls/ModUpdateConfirmDialog` |
| 下载缓存与来源凭证 | Nexus Mod 按 ModID/FileID 缓存，Collection 本体按 slug/revision 缓存；命中后跳过 API/浏览器与重复下载；`CollectionInstallService` 直接入口也会在登录检查前复用稳定缓存；缓存写入前会校验归档格式，HTML/错误响应不会污染缓存；归档识别以文件签名为准，`.7z` 扩展名但实际为 ZIP 的下载包不会走错解压器；兼容 WPF/Core 旧目录 `SVL/cache/nexusmods/downloads/mod_<ModID>_<FileID>.zip`，命中后自动提升到 Avalonia 缓存；命中后仍写入 `svl-source.json`，导出可保留 FileID | `NexusDownloadCache`、`NexusCollectionDownloadCache`、`ArchiveExtractor`、`ModpackInstallService`、`CollectionInstallService`、`DownloadPageViewModel` |
| 缓存管理 | 设置页可查看各类缓存统计，支持按保留时长清理过期文件、全量清理和打开 SVL 缓存目录；搜索/汉化缓存 TTL 与下载归档缓存 TTL 分开，后者默认 7 天；过期清理跳过正在占用的文件 | `CacheManagementService`、`SettingsPageViewModel`、`SettingsPageView` |
| Mod 目录整理 | 以 `manifest.json` 实际目录为安装根，去掉发行包外层目录；管理页读取 manifest 时跳过空的旧字段别名，继续尝试 `modVersion`/`releaseVersion` 等字段，并保留文件名回退；单个损坏/无权限 Mod 目录不会中断其余目录扫描；内置 Mod 解压失败会进入整合包失败列表，不再静默计入成功 | `InstallDownloadedModArchive`、`InstallBundledModDirectory`、`FeaturePagesViewModels` |
| Mod 依赖启用 | 启用单个或批量 Mod 前会汇总已安装但禁用的必需依赖，统一询问用户；确认后先启用依赖，再启用目标 Mod，取消则保留原状态 | `FeaturePagesViewModels`、`DialogService`、`Controls/DependencyEnableDialog` |
| 本地 Mod 冲突检测 | Mod 管理页可按需检测启用 Mod 的重复 UniqueID、缺失/禁用/版本不满足的必需前置，以及真实相对文件路径冲突；检测结果会在启用状态或列表刷新后失效，避免显示过期结论 | `Services/ModConflictAnalyzer`、`FeaturePagesViewModels`、`Views/VersionSettingsPageView.axaml` |
| 旧版依赖/冲突解析 | 修复旧 WPF/Core 分支的最低版本判断反向、短版本比较越界、循环依赖未返回、重复 ID 误报及绝对路径文件冲突漏报；Avalonia 主流程不直接引用该项目，但兼容代码已同步收紧 | `SVL.Core/Stardew/Mod/Dependency/ModDependencyResolver.cs`、`ModConflictDetector.cs` |
| 本地 Mod 安装 | 版本设置页本地安装同时支持 ZIP/CFModpack/7z，按文件签名选择解压器，并按实际 `manifest.json` 目录去除发行包外层目录 | `FeaturePagesViewModels.ImportModsFromLocalSource`、`ArchiveExtractor`、`ZipExtractor` |
| 删除/覆盖安全性 | 用户主动卸载、版本删除、普通 Mod 覆盖以及 Collection 覆盖/回滚中的用户目录统一优先移入系统回收站；临时解压、缓存和事务暂存目录仍按生命周期物理清理 | `RecycleBinService`、`FeaturePagesViewModels`、`InstancesPageViewModel`、`DownloadInstallService` |
| 导入/导出闭环 | 标准 `modpack.json`、`sources.json`、平台 ID/FileID、直链来源、配置、关系信息和动态 Icon；导出补全 FileID 时 Nexus 凭据与公开 CurseForge 查询分开处理；旧本地文件名/目录名及有效 Nexus 缓存中的 Nexus/CurseForge FileID 也会回填；CurseForge CDN 的 `/files/5312/529/` 路径会合并为完整 FileID `5312529`，不会截断为前段数字；来源清单还兼容常见 key→source 对象映射和 key→URL 简写；整合包独立 Mod 按条目写入 `sourceKind=modpack-entry`，嵌套子 Mod 强制写入 `parent-inherited`，旧目录名失效时按 manifest 的 UniqueID/Name 回退匹配 | `VersionSettingsPageViewModel`、`ModpackInstallService`、`DownloadInstallService` |
| 子 Mod 分组 | 管理页选择联动、折叠显示，导出保留父子关系；真实父 Mod 已携带压缩包和 `childMods` 时，嵌套子 Mod 不再重复导出为独立来源，避免回导时覆盖父级凭证 | `FeaturePagesViewModels`、`ModpackInstallService` |
| Mod 汉化管理 | 批量检测之外补齐选中 Mod 强制刷新汉化；列表保留 manifest 原文与汉化文本，可逐项切换中英文 | `FeaturePagesViewModels`、`VersionSettingsPageView.axaml` |
| 多线程进度 | 仅正常完成的文件计数，未完成显示最多 99%，整合包存在失败项时也保持 99%；下载完成与安装阶段分离，进入安装时不会沿用下载阶段的 100%，UI 回调切到调度器；异步排队的旧分片回调带阶段 epoch 校验，不会在安装/完成后把状态改回下载中；分片进度只在下载阶段展示，完成/失败/进入安装前会清理，最终完成快照才允许所有分片显示 100%；Mod 缓存还必须包含可解析 manifest | `DownloadPageViewModel`、`HttpDownloadService`、`ModpackInstallService`、`CollectionInstallService` |
| 重启后任务 | Pending 自动重新入队；中断中的任务转为可重试失败；状态文件串行保存；队列从后台完成回调泵入时先取得 UI 线程任务快照，任务桶、任务状态页和日志事件也会切回 UI 线程，避免 ObservableCollection 跨线程竞态；任务取消源使用线程安全字典，避免 UI 取消与后台清理并发读写；SMAPI 外部任务的状态提示也会切回 UI 线程；已入队的 Nexus Mod 任务若短期 CDN 地址在重启后失效，会按稳定 Mod/File ID 重新走 API 或 NXM 浏览器回退；带 ProjectID/FileID 的 CurseForge 任务也会重新解析 CDN 地址；取消令牌贯穿刷新流程 | `DownloadPageViewModel`、`DownloadTaskStateStore`、`MainWindowViewModel`、`TaskStatusPageViewModel` |
| 整合包失败汇总 | SVL/CurseForge Modpack 与 Nexus Collection 进入失败或部分完成状态时会弹出失败原因与日志入口；支持直接重试，重试后同一任务再次失败仍会重新提示 | `MainWindowViewModel`、`DialogService`、`Controls/ModpackFailureDialog` |
| WPF 配置迁移 | 支持 UTF-8/UTF-16、旧枚举、旧路径文件、实例/收藏/更新通道映射；会合并多个历史位置的 `instances.json`，并按多个 `default_instance.json` 恢复默认实例 | `LegacyConfigurationMigrationService` |
| 游戏窗口标题 | 版本设置和全局设置均提供占位符帮助；启动游戏后在 Windows 上等待游戏主窗口并应用 `<name>`、`<ver>`、`<smver>`、`<modscount>`，非 Windows 安全跳过窗口 API | `SVL.Core.Platform/Services/WindowTitleService`、`LaunchPageViewModel`、`SettingsPageView`、`VersionSettingsPageView` |
| 目标路径与实例注册 | 旧任务中的版本路径会回到 Base；不同 Base 可保留同名实例记录；Windows 自定义 Steam/GOG 安装位置会读取注册表，继续解析 libraryfolders.vdf；Xbox/Microsoft Store 常见的 `Content` 与 `XboxGames` 目录也会作为独立来源探测 | `InstanceRuntimePathResolver`、`GameInstallPathLocator`、`InstancesPageViewModel`、`ModpackInstallService`、`CollectionInstallService` |
| 整合包部分失败重试 | 已安装运行目录存在时按更新模式重试，复用当前 SMAPI，保留已有 Mods 与个性化 Icon；Collection 失败项会保留来源缺失、解析/回调失败或归档无 manifest 等具体原因 | `DownloadPageViewModel`、`ModpackInstallService`、`CollectionInstallService` |
| 图标优先级 | 自定义 Icon → SMAPI → Vanilla；SMAPI 与 Vanilla 使用独立文件名；版本设置页新装 SMAPI 后会把新隔离实例设为当前实例再刷新图标；安装流程生成的 SMAPI 默认图标带显式标记，整合包 Icon 可覆盖该默认图标但不会覆盖用户选择的图标，默认图标也不会被当作自定义 Icon 导出；Base 路径上的内置 `Vanilla.png` 占位不会再阻止 SMAPI 默认图标生成；生成标记存在时，SMAPI 预设不会遮住通用自定义图标 | `InstanceIconResolver`、`ModpackInstallService`、启动/实例/版本设置 VM |
| 设置恢复 | Avalonia 设置页已补齐 WPF 的恢复默认设置入口；恢复前确认，重置界面/下载/主题/启动器偏好并清除 Nexus 登录，同时保留实例路径、实例名称和收藏列表等用户数据 | `SettingsPageViewModel`、`SettingsPageView.axaml` |
| 兼容实例设置入口 | 兼容旧视图的 Avalonia 实例设置页已补齐窗口标题、自定义启动参数、服务器连接、收藏、Steam 覆写和恢复默认；恢复操作只清理实例偏好，不删除实例路径或 Mod 文件 | `InstanceSettingsPageViewModel`、`InstanceSettingsPageView.axaml` |
| 夜间主题与窗口控制区 | 主要弹窗使用动态主题资源；右键菜单显式走 Popup 主题并在应用级覆盖 `ContextMenu/MenuFlyoutPresenter`，避免独立 Popup 回落到浅色；控制按钮使用固定等宽列、统一 40×48 单元格、24×24 内容画布和布局取整，三种图形共用第 24 像素中心线；版本删除会清理只读属性，遇到短暂文件锁时先移出 `versions` 再后台重试；“跟随系统”现在读取 Avalonia 系统主题并监听后续切换 | `Controls/*.axaml`、`InstancesPageView.axaml`、`MainWindow.axaml`、`Resources/Theme.axaml`、`Services/ThemeService.cs`、`FeaturePagesViewModels` |

当前回归结果：`SVL.Avalonia` 随测试重新构建通过；迁移测试 **293 总计，其中 291 通过、2 跳过**。冲突检测还覆盖了同一循环依赖只生成一条链路结果；线上目录新增了可替换 HTTP fixture，已验证 CurseForge 搜索、详情文件列表、无文件提示和 FileID/直链保留，以及 Nexus GraphQL 搜索/详情和凭据传递；GitHub Mod 更新现在覆盖仓库规范化、稳定 Release 选择、预发布过滤、版本比较、任务状态持久化和压缩包来源写回；SMAPI GitHub 发布目录还覆盖了标准 releases 为空时回退 `releases/latest`、大小写不敏感字段和安装包地址选择；多线程最终快照和分片进度生命周期、窗口控件等宽布局及 Popup 菜单主题也有回归覆盖；窗口控制区已改为共享 24×24 矢量画布，最小化/最大化/关闭图形不再受不同 PNG 透明边界影响；CurseForge manifest 指定的 SMAPI FileID 现在会在版本目录不可用时优先从稳定缓存/直链安装，在线 CurseForge SMAPI 版本列表也会把 FileID 传递到稳定缓存；详情下载项统一兼容 Nexus `File <FileID>_...`、URL 编码文件名、`file-id` 查询参数和 `cf-<ProjectID>-<FileID>` 标识；WPF/Core 旧 `svl-source.json` 的 `modId/project_id/file_id/download_url` 别名也会被迁移读取；另存为任务也会清理生成式 FileID 前缀，只有直链时会从 URL 提取可读文件名，保证实际建议文件名可直接使用；Nexus 页面解析还兼容连字符和 URL 编码的查询键；CurseForge 整合包本体命中稳定缓存时会跳过 CDN 解析并入队，缓存被清理后任务会按 ProjectID/FileID 刷新地址；稳定缓存来源也有任务执行分支回归覆盖；SVL 来源下载现在对被包装成失败结果的瞬时 CDN/网络/归档错误进行有限重试，同时明确跳过来源缺失、登录、取消和浏览器回调失败；SVL/Collection 的旧来源现在还会从 `logicalFilename` 恢复 Nexus FileID，旧 Collection 多文件入口会将 `cf-项目ID-文件ID` 临时目录名还原为 manifest 名称，安装预览与实际落盘名称保持一致；Collection 完整导入链路也已覆盖“页面 URL + logicalFilename + Nexus 缓存”场景，验证安装过程不会错误打开浏览器；Collection 旧缓存解析也覆盖 `mod_<ModID>_<FileID>.zip` 命名；Collection 7z 导入还新增了真实解压、嵌套清单定位和 bundled Mod 去外层目录回归；本轮新增压缩包父 Mod + 兄弟目录 ContentPack 的来源树回归，并覆盖普通 Mod 在线安装路径，确认父级保留整合包/归档来源、子级写入 `parent-inherited`、旧更新状态清理且导出不会重复列出子级；新增加载期旧来源修复回归，确认同一项目混用旧/新 FileID 时会自动重建父子来源树；旧 Core 分支的改动仍受本机缺少 .NET Framework 4.8 Developer Pack 影响，无法进行完整项目编译。
最新测试统计（含本轮 manual 来源、Modpack 游戏版本、半包下载重试、兄弟目录子 Mod 来源树、普通 Mod 安装来源树、加载期旧来源修复及 CurseForge 整合包游戏版本详情回归）：迁移测试 **297 总计，其中 295 通过、2 跳过**。
本轮界面回退：撤销此前生成式 PNG 图标替换，恢复 `Resources/Icons.axaml` 中的原有矢量资源及动态颜色绑定；标题栏仍保留统一 24×24 画布和等宽控制列，避免回退图标后重新引入三个窗口按钮的对齐问题，并新增回归断言禁止视图重新引用 `Assets/Icons/Generated`。
本轮修复：Modpack/Collection 的游戏版本只接受 API 明确字段，不再从整合包名称、摘要或文件名推断版本；普通 Mod 仍保留文本兜底。
本轮下载链路修复：HTTP 层对响应提前结束、连接重置和瞬时 408/429/5xx 做最多 3 次有限重试；403/404/416 等确定性错误仍交由来源刷新或上层处理，并新增半包恢复回归。
本轮新增验证：来源写回新增“旧父目录路径 + 子 Mod 旧独立来源”回归，确认实际目录按 manifest 身份恢复、子 Mod 的独立来源被清理、父级来源保留。
本轮继续修复复合归档兼容：当一个压缩包展开为一个 DLL 父 Mod 与多个一级 ContentPack 兄弟目录时，按 manifest 的 `ContentPackFor` 关系建立父子来源树；旧 `childMods` 路径失效时可按 UniqueID/Name 找回兄弟目录，且兼容导出/回导。
CurseForge 整合包搜索与详情现在都只读取 API 的 `latestFilesIndexes.gameVersion`/文件 `gameVersions`；不会再从整合包名称、摘要或文件名提取整合包自身版本作为星露谷物语版本。
来源兼容继续补齐：空标准字段不再遮蔽有效别名，key→source 映射布局使用同一描述符识别规则，覆盖仅含 site/project/file 的条目。Collection 仅有 ModID 时收到的浏览器回调会继续携带 NXM 一次性凭据；此分支已修复传参，真实账号网络回调仍待验收。
Collection 单 Mod 下载也复用同一失败重试策略：瞬时 CDN/网络/归档失败有限重试，来源缺失、登录、取消和浏览器回调未完成不会重复触发。
旧的多文件 Collection 安装入口也改为复用普通 Mod/整合包的有效 manifest 根筛选；外层 Name-only 发布清单不会再和内层真实 Mod 一起落入 `Mods`，并已有嵌套归档回归覆盖。
本轮继续补齐 CurseForge URL 级来源恢复：导入描述符和导出页现在都能从 `api.curse.tools/.../mods/<ProjectID>/files/<FileID>`、CurseForge 文件页及 Forge CDN 路径恢复数字 ID；仅含 CDN 的条目至少保留完整 FileID，避免再次导出时把稳定来源降级为未知。详情页、导出页与整合包安装器共用下载项 FileID 解析器，兼容 Nexus `File <FileID>_...`、URL 编码文件名、`file-id` 查询参数和 `cf-<ProjectID>-<FileID>` 标识。
新增回归覆盖 SVL 来源条目仅含 CurseForge ProjectID/FileID，以及同时带过期直链的缓存安装；均验证实际 Mod 文件及来源凭证。SVL/Collection 现在在在线解析之前复用 CurseForge 缓存，SVL 直链成功安装后补写平台缓存；Collection 旧入口也统一读取新旧两种 Nexus 缓存文件名，历史 WPF 缓存命中后能继续回写 FileID。配置、实例列表和任务状态保存采用唯一临时文件并刷新到磁盘后替换。
断点续传的 `.part.json` 及迁移完成标记也采用相同的原子写入，进程中断时不会以半截 JSON 覆盖可恢复状态。

另存为任务恢复时不再强制按 Mod manifest 校验最终文件；只要目标文件已完整落盘且不存在 `.part`/`.part.json`，即可跳过重复下载，仍在写入的半成品不会被误复用。
Nexus/CurseForge 下载解析、HTTP Range 探测及缓存命中复制会保留取消异常，取消不会被误判为普通解析失败或继续触发回退下载；Mod 来源凭证也使用原子写入，避免进程中断留下半截 JSON。
导出选择项与导出审计项现在共用 Nexus/CurseForge 网页地址过滤规则，文件页不会被误报为可直接下载的压缩包；来源文件名会随 `sources.json` 保留并在回导时写回 `svl-source.json`。
在线搜索的客户端筛选已调整为按来源分批拉取、先过滤再分页；`HasMore` 根据实际返回批次和匹配结果计算，并受本地候选上限保护，避免末页误显示或无限请求。真实线上接口的稀疏筛选和深页仍待账号网络环境验收。
Nexus 整合包搜索的关键词过滤现在会按原始 GraphQL offset 继续读取批次，再对匹配结果分页；不会因为前一批无匹配项就把后页结果误判为空，同时页码计算已避免整数溢出。
Avalonia 的 Nexus 下载地址解析现在兼容顶层数组、`download_links` 数组/对象、字符串 URL、键值映射和 `data/result/links` 包装，并允许浏览器回调携带的 NXM 一次性 key 在无账号凭据时完成 Collection 地址解析；历史 NxmCollection 任务缺少短期 CDN 地址时会按缓存、API、浏览器回退顺序恢复，不再直接终止且不可重试。
Windows 路径探测补齐 SteamPath/InstallPath 与 GOG PATH 注册表候选；自定义 Steam 库的 `libraryfolders.vdf` 解析已有独立回归。
Windows 路径探测同时补齐 Xbox/Microsoft Store 的常见安装目录，并在实例页以独立来源展示，避免把 Store 版本误标为 Steam 或 GOG；Content 子目录优先于包根目录，受保护目录的枚举异常也会被隔离，已有候选枚举与游戏标记回归。
新增回归覆盖整合包根目录 Icon 优先于嵌套 Mod Icon、生成的 SMAPI 默认图标可被包内 Icon 替换且用户自定义图标在重试时保留；旧的统一搜索入口也已委托同一筛选分页实现，避免“先分页再过滤”的结果漂移。
整合包安装不会再把只有 `svl-source.json`、缺少有效 `manifest.json` 的残留目录当作已安装 Mod，从而避免跳过实际下载；Collection 清单同时兼容标准的嵌套 `source` 与旧导出常见的顶层 `type/modId/fileId/url` 字段，并在混合布局时合并有效字段。在线搜索与详情页还增加了请求代次保护，旧响应不会覆盖最后一次搜索/详情；来源未知或没有可安装下载项时会显示明确提示。Mod 管理/详情页还会把含子清单的 Name-only 外层发布目录降级为包装目录，优先读取内层真实 Mod manifest。
下载归档清理已与旧版搜索/汉化缓存 TTL 分离，默认保留 7 天；Mod 覆盖安装改为暂存目录事务替换，复制失败时保留原目录，避免整合包重试把原有可用 Mod 删除。普通 Mod 下载安装也不会在压缩包校验前删除同名目录；无效归档回归已确认旧 Mod 仍然保留。来源条目即使只有 `directoryName` 也会进入下载与凭证写入流程。安装器也会跳过含内层 manifest 的 Name-only 外层包装目录，避免外层清单遮蔽真实 Mod；对应 Name-only 外层归档已有实际安装回归。
Collection 的 bundled Mod 现在会分别记录成功与失败项；部分 bundled 目录损坏时仍可完成其余安装，但失败名称和缺失 `manifest.json` 原因会进入任务报告，不再出现“安装 0 个、失败原因未列出”的静默结果。
Collection 来源归一化还会把空的顶层 `type/url/fileSize/logicalFilename` 视为缺失，继续合并嵌套 `source` 中的有效值，避免第三方导出用空标准字段遮蔽真实下载来源。
旧版 WPF Collection 下载任务现在兼容 `download_links` 的数组、对象和字符串形态，并将只有可解析 Mod 列表的响应转换为标准 `collection.json` ZIP；归档下载改为流式写入，进度按总字节数计算，取消时保留取消状态并清理临时文件。
SVL 来源描述符读取时会跳过 `0`/负数的标准 ID，继续尝试 `project/file` 等历史别名；带 URL 的 `Unknown` 来源允许重新推断平台，但没有 URL 的未知双 ID 不会被武断地当作 Nexus。
整合包清单未提供 `smapi_version` 且版本目录请求返回空时，SVL 与 CurseForge 安装器现在会独立请求 GitHub latest 稳定发布，再继续执行安装包结构校验；只有未获得可校验的官方包时才停止安装，避免把网络目录短暂为空直接误报为整合包清单错误。
SMAPI 版本目录在安装首屏会优先尝试官方 GitHub 稳定发布的 latest，再回退 CurseForge，避免目录接口短暂为空时先卡在不可用的中间结果；GitHub 发布字段按大小写不敏感读取，版本目录、latest 请求和安装器都传递取消令牌，取消不会被吞掉后继续触发网络兜底。
在线 Mod 未选中主页版本时的 SMAPI 目标列表已抽成统一构造逻辑：保留所有有效 SMAPI 实例的完整路径、补充 SMAPI 版本标识并按路径去重；同一 SMAPI 版本的直接下载/恢复还增加了进程内并发锁，避免固定缓存文件被并发覆盖。
导出包再次导入的回归已补充：第二次导入同一压缩包会命中 Nexus 缓存、保留 FileID 和来源凭证，不再重复打开浏览器或下载；同时验证了 bundled Mod 的成功/失败统计和 manifest 落盘。
任务状态详情新增目标 Base 路径与实际安装目录的完整换行显示，导入整合包时无需只依赖日志即可确认最终落盘位置。
现有警告为 NuGet 的 `Tmds.DBus.Protocol 0.20.0` 安全公告，不是本轮代码错误。
旧 SMAPI 任务若在 `TargetGamePath` 中保存版本目录，真正调用安装器前也会再次归一化为 Base 路径，防止恢复任务产生嵌套 `versions`。
Windows Debug 单文件发布脚本已在本轮最终构建后重新执行通过；产物为
`artifacts/SVL_v1.2.0.0_debug_Windows_x64.zip`（压缩包包含单文件 EXE 及调试符号）。
迁移文档约定的 `scripts/package-avalonia.ps1` 兼容入口已补齐，并委托根目录
`build.ps1`，避免文档命令因脚本缺失而无法打包。
Unix 主机对应的 `scripts/package-avalonia.sh` 入口也已补齐，统一委托
`pwsh build.ps1 -Targets macos`，缺少 PowerShell 时会给出明确提示。

## 仍需继续完善

1. 使用真实导出的 SVL/CurseForge/Nexus fixture 做一次端到端导入→安装→导出→再次导入，重点确认来源缺失、下载失败和旧包布局不会静默“安装完成”。当前已补本地直链端到端 fixture、导出包回导 fixture（覆盖自定义 Icon、配置覆盖、Nexus 缓存和 FileID）、真实 Nexus 文件页来源（`mods/29868?tab=files&nmm=1` + `File 7448774_...zip`）缓存命中、旧 WPF Nexus 缓存自动提升、直链来源保存/导出、来源缺失与缺失 `modpack.json` 的显式失败、设置目录按 manifest 映射、损坏/兼容 manifest、数字/字符串 FileID 兼容、无扩展名 7z、ZIP 越界防护、大小写不同的 manifest/Icon、Collection 镜像择一下载、Collection 外层目录下 `bundled` 解析、Collection 旧 source 字符串/URL 兼容，以及部分失败重试的更新模式回归；任务状态还会保存安装目录、速度、大小与子进度。近期又补充了生成式目录名（`cf-项目ID-文件ID`/`File 文件ID_...`）按 manifest 名整理、选中任务状态实时刷新、失败进度封顶 99%，CurseForge 页面/API 地址禁止直接下载、迁移标记允许新旧来源增量补迁移、迁移目标丢失后按旧来源恢复、CurseForge manifest 数字/字符串 ID、`files: null` 与 Collection 单字符串 `gameVersions` 的回归，以及“默认 SMAPI 图标可被包内 Icon 替换、用户自定义图标在重试时保留”的回归；`sources.json` 现兼容数组、常见对象包装、单来源对象、key→source 映射和 key→URL 简写，并可回退读取 `modpack.json.mods`，同时兼容 `site/provider/project/file` 等常见来源别名和真实 CurseForge CDN 路径回填 FileID；本地 SMAPI 复用会跳过损坏/无权限候选继续尝试可用实例；新增覆盖外层非目标 JSON 遮蔽内层有效 CurseForge/Collection 清单、未知 SMAPI 缓存拒绝复用、重复浏览器等待者和任务重试报告归属的回归。导出端还会从旧 Nexus 文件名/URL、CurseForge `cf-project-file` 目录名补回本地 FileID，减少不必要的线上查询；新增回归确认导入清单中的 NXM 一次性凭据会保留到解析请求；Mod 缓存复用现在必须通过有效 manifest 校验，延迟删除目录也会在启动/刷新时再次清理；SMAPI 默认图标写入会在资源流关闭后验证目标文件与实际解析路径，并兼容框架尚未初始化时的显式资源加载；SMAPI 官方回退兼容 `4.5.1.0` 到 `4.5.1` 这类 Release 标签差异；迁移还会在当前应用目录、工作目录与相邻 `SVL.Desktop`/WPF 目录内有限探测旧 `SVL/instances.json`，覆盖并排发布场景；Collection 稳定缓存命中现在覆盖直接安装入口，且 `.7z` 文件名不会再覆盖实际 ZIP 签名判断；本轮又补充了残留 source-only 目录过滤、Collection 顶层/嵌套来源字段归一化、bundled Mod 部分失败的逐项报告，以及管理页/导出页遇到单个无权限或重解析点目录时继续扫描其它 Mod 的安全遍历。
2. 补齐线上搜索的详情和下载选项回归；整合包分页已接入 CurseForge 服务端 index/pageSize，并让 Nexus 按目标页偏移增加候选拉取量，同时修正末页 HasMore 判断；Nexus 页面仅有 Mod ID 时已接入 API/浏览器回退；兼容搜索入口已补齐筛选项与热门整合包首屏加载。下载页目录项和遗留搜索入口已保留结构化资源身份，详情展开/跳转优先使用结构化请求；详情下载 URL 会先剥离 `~~` 元数据，避免浏览器打开入口与安装入口行为不一致；CurseForge 解析层现在统一拒绝文件页/API URL，详情安装、批量更新、Modpack/Collection 安装共享同一安全边界；SMAPI 目录请求新增代理失败后的直连回退，并兼容 CurseForge 响应的大小写、`data/result/files/items` 多层包装及字符串 ID；SMAPI 目录给出网页地址时会回退到稳定 Forge CDN 路径；本轮补充搜索/详情请求代次保护，旧响应不会覆盖新结果，来源未知或没有可安装文件时会显示明确提示；旧 SMAPI 任务若只保存版本路径或使用“SMAPI 版本 - 实例名”任务名，现在会恢复实例名并把目标路径归一化到 Base，避免下载后再次弹窗或生成嵌套 versions。Nexus/CurseForge 的真实下载仍需要用户登录状态或可用网络，当前只能做协议和解析层验证。
3. 在可见的 Windows Avalonia 窗口中完成 UI 冒烟，确认三个窗口按钮的视觉中心、右键菜单命中区域和深色弹窗实际渲染；代码侧已改为显式右键打开并把菜单样式提升到应用级，Icon 选择项、本地 Mod 详情、详情页图标/分隔线/加载遮罩、SMAPI 预发布标签和托管弹窗背景已统一使用动态主题资源，自动化环境目前仍无法稳定枚举原生 Avalonia 窗口。
4. 处理旧 WPF 项目自身的编译债务；该项不应阻塞 `SVL.Avalonia.csproj` 和迁移测试。

## 下一轮顺序

近期补充：SMAPI 历史 Nexus 双层 ZIP 缓存会在 `install.dat` 校验前自动归一化，Modpack/Collection 共用该处理；重启后重试外部 SMAPI 任务会根据已有实例目录进入更新模式；整合包安装阶段失败时会优先复用已落盘且类型有效的归档，即使版本目录尚未创建也能恢复安装，损坏归档会回到重新下载流程；在线 Collection、NXM 与本地 Collection 统一走同一安装器，取消更新 Collection 不会清理原实例，NXM 导入任务会持久化 Nexus 来源平台；旧清单的 Nexus 双 ID 直链会在下载前恢复平台身份，避免回导后丢失来源；导出页对仅保留 NXM、Nexus 文件页或 `cf-project-file` 标识的旧来源会先恢复平台与 ID，再补全 FileID，避免导出后退化为未知来源；恢复任务时普通 Mod、另存为和 SMAPI 会按动作类型复用已经落盘的完整归档，损坏或类型不匹配的文件会回到网络下载；CurseForge 整合包本体即使只有已持久化的 ProjectID/FileID，也会恢复为稳定缓存来源，缓存缺失后自动刷新 CDN；下载回调增加阶段 epoch 防止迟到进度覆盖安装状态，Mod 缓存复用前会验证 manifest，删除失败留下的临时版本目录会在下次启动/刷新时清理。

1. 用真实 SVL/CurseForge/Nexus fixture 继续固定整合包导入/导出闭环及失败项重试，并把“缺少来源”纳入任务级重试提示；当前离线导出包回导已覆盖，错误格式包也会在清单阶段停止，部分成功任务已经可以真正更新重试。SMAPI 与普通 Mod 无 FileID 的旧 Nexus 来源现在都会按 ModID 扫描并在远程目录不可用时复用；CurseForge 导出在可联网时可按项目补全 FileID，并兼容两种 curse.tools 文件列表接口；CurseForge manifest 固定的 SMAPI FileID 也已接入稳定缓存/直链优先路径；任务页已提供安装报告/重试报告入口。CurseForge 页面/API 误下载已在远端解析层和各安装入口封口；归档外层存在无效或非 Mod `manifest.json` 时会先过滤有效内层清单，避免内层 Mod 被错误遮蔽；SMAPI 历史 Nexus 双层 ZIP 缓存会在 install.dat 校验前自动归一化，Modpack/Collection 共用该处理。
补充：上述归档入口的 manifest 扫描现在也统一经过 `ModpackInstallService.EnumerateFilesSafe`，逐目录隔离权限/损坏错误并跳过重解析点，Collection 清单定位和普通 Mod 安装不会因单个异常目录丢失整个包。
2. 完成真实 UI 冒烟后的窗口控制区、菜单和图标视觉修正（详情页固定背景已先改为动态主题资源）。
3. 用真实在线返回 fixture 继续审计 Mod/Modpack 搜索与详情下载链路，验证分页、权限失败、无文件和实际下载项在不同来源下的展示与安装结果。
4. 最后清理迁移兼容分支和文档，重新执行构建、迁移测试与打包脚本；本轮打包已完成，后续仅需在代码继续变化后重复执行。
本轮补充：嵌套 ContentPack 的父级来源识别已与平台解耦；单独检查更新时同样显示“继承父 Mod 来源”，并新增 Nexus 来源回归测试。
本轮验证统计更新：跨平台嵌套子 Mod 来源回归加入后，迁移测试为 **294 总计，其中 292 通过、2 跳过**。
本轮冲突检测收敛：旧分析入口不再读取 Mod 文件，用户界面只依据社区 `hardConflicts`/`functionalOverlaps` 字段，`svl-source.json` 不会再制造文件冲突。

本轮实际目录兼容修复：针对旧数据中 ContentPack 被错误写成
`sourceKind=modpack-entry`、同时复制父整合包 project/file ID 且没有独立
`fileName/downloadUrl` 的情况，更新检查现在会结合 manifest 的
`ContentPackFor` 判定为父 Mod 的继承来源，不再把整合包版本误报成子 Mod
的可更新版本；对应旧 `MarketTown` 目录形态已加入回归测试。

本轮验证统计更正：加入旧 `modpack-entry` ContentPack 兼容回归后，迁移测试为
**295 总计，其中 293 通过、2 跳过**。

本轮新增 Collection 任务详情回归：验证处理中条目会显示为当前 Mod，并在完成后清除当前标记；当前迁移测试为
**296 总计，其中 294 通过、2 跳过**。

本轮继续补齐 Collection `manual` 来源边界：任务详情新增“选择文件并安装”，
可将浏览器下载的 Mod 归档直接安装到该任务保存的实际实例目录；安装前仍执行
冲突比对与覆盖前备份，安装后按父 Mod/ContentPack 目录树写入来源凭证。

本轮继续收口 Collection 失败重试：显式重试会保留已成功条目的临时状态，
并由安装服务再次校验来源凭证或有效 `manifest.json` 后跳过；已安装条目不再
因其它 Mod 失败而重复下载/覆盖，残缺目录仍会回到正常安装流程。
