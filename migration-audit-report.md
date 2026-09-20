# SVL Avalonia 迁移审计

更新时间：2026-09-19

本报告以当前工作树为准，重点覆盖 Avalonia 主流程与 WPF 旧配置兼容，不把
`SVL.sln` 中与 Avalonia 无关的旧 WPF/SMAPI 编译问题误计入迁移结果。

## `upstream/main` 分支对照结论

对照基准为当前 `Avalonia-Dev` 工作树、`upstream/main`（`19ef4ef`）、
`origin/main`（`7e92bdc`）以及共同祖先（`7e92bdc`）。共同祖先之后，
`upstream/main` 只有一个 README 说明性提交，`origin/main` 仍停在共同祖先，
两者都没有新增 WPF/Core 业务代码；
`main` 仍是 .NET Framework 4.8 + WPF 旧架构，且不包含 `SVL.Avalonia`。
因此，直接用 `git diff HEAD..upstream/main` 会把 Avalonia 工程显示成“被删除”，
这表示分支历史已经分叉，不表示 main 存在一批尚未移植的新增功能。
2026-09-15 再次查询 GitHub 实时 refs：`upstream/main=19ef4ef5159625d2cb00c018318854be374d87cc`，
`upstream/Dev-Avalonia` 与 `upstream/Avalonia-Dev` 均为当前 `a5f96697cdf586d4256f940616a06a8deb1b9cdf`；
共同祖先仍为 `7e92bdc5b57d8cf2181bab17f0819df8af7b2c01`，祖先之后的 `main` diff 仅有 `README.md`。

当前上游 Avalonia 提交的 GitHub Actions 也已复验：`a5f96697` 的 `Package Avalonia`
工作流于 2026-09-15 成功完成，Build and test 通过，4 个平台打包产物均生成
（Windows x64、Linux x64、macOS x64/ARM64；该 CI 运行不包含当前尚未提交的工作树改动）。这验证 CI 构建/打包，不替代真实 Nexus/CurseForge
账号流程或 Windows Acrylic 视觉验收。
本轮将已验证工作树提交为 `9a09871`、`358401d`，推送到上游仓库的安全分支
`codex/avalonia-dev-20260919`，并手动触发运行 `35434301865`。该运行的
Windows/Linux/macOS 测试矩阵、Linux ZIP、Windows 包、macOS 双架构 `.app` 与 DMG
均成功完成，且 macOS 的 NXM scheme 与最低系统版本校验通过；结果已由
`2e7c391` 记录。`origin/Avalonia-Dev`
历史与当前分支不相容，因此没有强推覆盖原远端分支。

旧 WPF 页面和 Avalonia 页面已经按功能合并迁移。WPF 的本地 Modpack 管理页在
`upstream/main` 中本身仍是“功能开发中”占位页；Avalonia 已将可用的导入入口放在
实例页、导出入口放在版本设置页，并将在线搜索/详情放在下载流程中，不能把这个
占位页当成遗漏的成熟功能。

目前确认的真实缺口/边界如下：

| 项目 | 对照结论 | 处理计划 |
| --- | --- | --- |
| `manifest.json` 的 `UpdateKeys=GitHub:...` 自动检查更新 | Avalonia 与旧 WPF/Core 的 `ModManager` 均已补齐 GitHub 仓库/release 解析、稳定版本比较和 Release 压缩包选择；旧版批量更新也会使用资产直链完成下载、安装和原目录替换 | 后续只需在真实 GitHub 仓库做一次端到端 UI 验收；无 Release 压缩包时会明确提示，不把源码包误当 Mod 安装包 |
| Nexus Collection 的 `manual` 来源 | WPF 安装器仍是 TODO；Avalonia 已覆盖 NXM、API、浏览器回调及 HTTP 直链，并将 `manual` 网页/无来源条目标记为需手动处理；只有明确归档直链才自动下载 | 任务页保留来源地址并提供“打开来源”，用户完成下载后可拖入当前实例 Mods 页面；不把网页地址当压缩包 |
| Nexus Collection 非 Premium 逐项向导 | WPF 的 `NexusCollectionWizardTask` 还提供阶段分页、上一页/下一页和手动跳过可选 Mod；Avalonia 继续使用统一安装队列，任务详情已展示当前 Mod、阶段、可选/必需状态、逐项结果，并支持打开来源或选择本地归档恢复；可选 Mod 自动失败现在进入“待处理”，用户可明确选择“选择文件并安装”或“跳过可选 Mod”，选择会持久化并在重试时生效；任务详情现已补齐 Collection Mod 列表分页、上一页/下一页导航，不改变统一队列的实际安装顺序 | 保持统一队列作为默认路径；后续仅需进行真实 Collection 的可见 UI 验收，不恢复旧任务类型 |
| 启动器“发现新版本后自动下载”设置 | WPF 只保存 `AutoDownloadUpdate` 字段，旧启动流程未消费；Avalonia 已迁移该设置并接入更新弹窗 | 发现新版本后可自动下载首个发布资产；下载完成仍需用户点击“安装并重启”，不会静默执行安装 |
| WPF 系统托盘设置 | `main` 的 WPF 仅保存 `MinimizeToTrayOnStartup/OnClose` 两个字段，没有 `NotifyIcon/TrayIcon`、菜单或关闭/恢复运行时逻辑；Avalonia 已迁移字段和设置界面，并补齐了独立的跨平台托盘运行时 | Avalonia 已提供托盘图标、“显示主窗口/退出”菜单、启动/关闭隐藏及不支持平台回退；Windows/macOS/Linux 实机托盘图标和原生菜单仍需验收，不计为 `main` 漏迁移 |
| 游戏启动后启动器可见性行为 | WPF 的 `LauncherVisibility` 五种行为此前只完成了配置对照，Avalonia 未接入实际启动生命周期 | 已迁移立即关闭、隐藏后随游戏退出关闭、隐藏后随游戏退出恢复、最小化、保持不变；旧枚举数值/字符串均可迁移，并由游戏进程退出事件驱动恢复 |
| 本地 Modpack 管理列表 | `main` 的 `ModpacksLeft/RightViewModel` 仍只显示“开发中”；不是可迁移的完整功能 | 继续使用 Avalonia 的实例导入、版本设置导出和在线 Modpack 页面；未来若需要再设计独立列表 |
| WPF 个性化字段 `PrimaryColor` | 旧配置和设置 ViewModel 有该字段，但旧 `ThemeService` 没有实际应用它；它不是 `main` 中可工作的独立功能 | 已补齐为可选自定义强调色：设置页支持 `#RGB/#RRGGBB`，留空跟随当前主题；旧默认值 `#7C4DFF` 不会改变 Stardew 默认配色；主题/深浅色切换后保持覆盖，并有无效输入保护与回归测试 |
| WPF `EnableTransparency` | 原先仅 Splash 使用透明窗口，主窗口没有动态透明开关 | 已补齐旧配置迁移、设置页开关、主题背景透明度和主窗口透明级别；不支持透明的窗口后端按资源层回退为不透明 |
| WPF `EnableAnimations` | 旧 WPF 用它控制主题切换动画；Avalonia 配置模型曾保留字段，但设置页和运行时过渡没有接入 | 已补齐设置页开关、自动保存/旧配置读取，并通过动态 `Transitions` 资源即时控制导航、窗口控制、任务、Mod 行和通知动画；Headless 回归覆盖关闭与恢复 |
| WPF `ShowUpdateNotification` | WPF 设置页保存了该字段；Avalonia 已迁移字段、旧配置导入、设置页自动保存，并在启动检查发现更新时尊重该开关；设置页手动检查不受影响 | 已完成；关闭后仅抑制启动更新通知，保留手动检查入口 |
| WPF Nexus 邮箱/密码字段 | 旧配置保留邮箱和密码字段，Avalonia 采用 API Key/OAuth 登录；直接迁移旧密码会扩大敏感信息暴露面 | 不迁移邮箱/密码；保留 OAuth/API Key 兼容，真实登录验收列入线上测试 |
| WPF Nexus OAuth 头像与 API 限额卡片 | WPF 会读取 OAuth `picture`、缓存头像并显示 API 每小时/每日用量；Token 失效时仍保留已缓存账号资料并提示重新登录 | 已迁移头像 claim/旧缓存兼容、后台缓存、小时/每日用量及限额快照；失效 Token 会保留资料卡但不作为可用凭据；Avalonia 的 Nexus 目录、NXM、登录验证请求统一记录 `X-RL-*` 响应头 |
| NXM 浏览器协议注册 | WPF/Core 仅实现 Windows 注册表注册，Avalonia 原先在 Linux/macOS 也统一报告不支持 | 已保留 Windows 注册表路径；Linux 按 `XDG_CURRENT_DESKTOP` 有序列表维护/检查桌面专属 `mimeapps.list`，会跳过不存在或未声明 NXM 的处理器再按优先级回退；macOS `.app` 声明 `CFBundleURLTypes` 并通过 Launch Services 设置默认处理器。Linux/macOS 实机默认应用行为仍需在对应桌面环境验收 |
| WPF 专属实现细节 | WPF 的 `ImageCacheService`、`SearchCacheService`、下载任务类、NXM 注册、实例/启动服务等已由 Avalonia 服务或 `SVL.Core.Platform` 重构承接，类名不同不代表缺失 | 以行为验收和回归测试为准，不按一一同名复制 |
| 完整线上/可见 UI 验收 | 不是分支迁移代码缺口；Windows 核心窗口/菜单已实机复验，主窗口后端曾报告 `AcrylicBlur`，但透明表面调整后仍需无遮挡截图确认最终观感 | 发布前仍需真实 Nexus/CurseForge 账号验收、Linux/macOS NXM 跳转实测及不同实体 DPI/窗口状态验收 |
| 跨平台发布包完整性 | GitHub Actions 之前对 Linux/macOS 直接执行 `dotnet publish` 并上传目录：Linux 下载后可能丢失 apphost 执行位，macOS 也没有带 `nxm` 声明的 `.app` 包 | 工作流改为复用根目录打包脚本：Linux 上传含 `run-svl.sh` 的 ZIP；macOS 生成 ARM64/x64 `.app` 和 DMG，并在 CI 校验两个 Info.plist 声明 `nxm`。运行 `35434301865` 已实跑通过 |
| 旧 WPF .NET Framework 4.8 编译 | 已通过 `Microsoft.NETFramework.ReferenceAssemblies 1.0.3` 补齐 SDK 构建所需引用程序集，并修复 `SharpCompress` 升级后的旧 API/`Math.Clamp` 兼容问题；但干净检出仍缺少被 `.gitignore` 排除的外部 `SVL.Core.Stardew.Mod.SMAPI` 适配源，本机工作树若存在该源才能完成旧 WPF 构建 | 不擅自把外部 SMAPI 源纳入 Avalonia 迁移；旧 WPF 继续作为独立历史工程处理，不阻塞 Avalonia |

## 已完成并已验证

2026-09-15 透明配置复核：主窗口 `Background="Transparent"`，独立 `WindowBackdrop` 负责主题着色，并设置不透明的 `TransparencyBackgroundFallbackBrush`；透明级别按 `[Transparent, AcrylicBlur]` 顺序请求。Headless 透明行为回归通过，全量迁移测试 336 项（334 通过、2 跳过），Debug 构建 0 警告/错误。Windows 主窗口日志报告 `actual=AcrylicBlur`，但当时截图被上层窗口遮挡，且内容卡片保持不透明；这只能证明后端接受透明级别，不能证明主页面透底足够明显。未修改用户主题/透明开关设置。2026-09-16 针对卡片遮挡补充窗口级半透明资源，最终观感仍待无遮挡实机截图验收。

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
| 缓存管理 | 设置页可查看各类缓存统计，支持按保留时长清理过期文件、全量清理和打开 SVL 缓存目录；搜索/汉化缓存 TTL 与下载归档缓存 TTL 分开，后者默认 7 天；过期清理跳过正在占用的文件；远程 Mod、Modpack、SMAPI 图片统一显示为“图片/图标缓存”，沿用旧目录名以兼容已有缓存 | `CacheManagementService`、`SettingsPageViewModel`、`SettingsPageView`、`AssetImageConverter` |
| Mod 目录整理 | 以 `manifest.json` 实际目录为安装根，去掉发行包外层目录；管理页读取 manifest 时跳过空的旧字段别名，继续尝试 `modVersion`/`releaseVersion` 等字段，并保留文件名回退；单个损坏/无权限 Mod 目录不会中断其余目录扫描；内置 Mod 解压失败会进入整合包失败列表，不再静默计入成功 | `InstallDownloadedModArchive`、`InstallBundledModDirectory`、`FeaturePagesViewModels` |
| Mod 依赖启用 | 启用单个或批量 Mod 前会汇总已安装但禁用的必需依赖，统一询问用户；确认后先启用依赖，再启用目标 Mod，取消则保留原状态 | `FeaturePagesViewModels`、`DialogService`、`Controls/DependencyEnableDialog` |
| 本地 Mod 冲突检测 | Mod 管理页可按需检测启用 Mod 的重复 UniqueID、缺失/禁用/版本不满足的必需前置，以及真实相对文件路径冲突；检测结果会在启用状态或列表刷新后失效，避免显示过期结论 | `Services/ModConflictAnalyzer`、`FeaturePagesViewModels`、`Views/VersionSettingsPageView.axaml` |
| 旧版依赖/冲突解析 | 修复旧 WPF/Core 分支的最低版本判断反向、短版本比较越界、循环依赖未返回、重复 ID 误报及绝对路径文件冲突漏报；Avalonia 主流程不直接引用该项目，但兼容代码已同步收紧 | `SVL.Core/Stardew/Mod/Dependency/ModDependencyResolver.cs`、`ModConflictDetector.cs` |
| 本地 Mod 安装 | 版本设置页本地安装同时支持 ZIP/CFModpack/7z，按文件签名选择解压器，并按实际 `manifest.json` 目录去除发行包外层目录 | `FeaturePagesViewModels.ImportModsFromLocalSource`、`ArchiveExtractor`、`ZipExtractor` |
| 删除/覆盖安全性 | 用户主动卸载、Base SMAPI 卸载、版本删除、普通 Mod 覆盖、旧 WPF 下载任务覆盖、嵌套 Mod 修复中的同名目标及 Collection 覆盖/回滚中的用户目录优先移入统一回收站服务；Avalonia SMAPI 更新先在同卷暂存旧运行时条目，再整体送入回收站，失败则恢复原文件并中止更新；旧 WPF 实例删除先安全移除 junction，再回收完整版本目录；失败时新建的版本目录也移入回收站，临时解压、缓存及已搬空的事务暂存目录仍按生命周期清理 | `SVL.Core.Platform.Services.RecycleBinService`、`SmapiInstallService`、`SettingsService`、`ModManager`、`ModDownloadTask`、`FeaturePagesViewModels`、`InstancesPageViewModel`、`DownloadInstallService` |
| 导入/导出闭环 | 标准 `modpack.json`、`sources.json`、平台 ID/FileID、直链来源、配置、关系信息和动态 Icon；导出补全 FileID 时 Nexus 凭据与公开 CurseForge 查询分开处理；旧本地文件名/目录名及有效 Nexus 缓存中的 Nexus/CurseForge FileID 也会回填；CurseForge CDN 的 `/files/5312/529/` 路径会合并为完整 FileID `5312529`，不会截断为前段数字；来源清单还兼容常见 key→source 对象映射和 key→URL 简写；整合包独立 Mod 按条目写入 `sourceKind=modpack-entry`，嵌套子 Mod 强制写入 `parent-inherited`，旧目录名失效时按 manifest 的 UniqueID/Name 回退匹配；同一项目但不同 FileID 的独立条目不会被旧 `childMods`/`parentMod` 关系覆盖，同归档的父子条目仍继承父来源 | `VersionSettingsPageViewModel`、`ModpackInstallService`、`DownloadInstallService` |
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
| 夜间主题与窗口控制区 | 主要弹窗使用动态主题资源；右键菜单显式走 Popup 主题并在应用级覆盖 `ContextMenu/MenuFlyoutPresenter`，避免独立 Popup 回落到浅色；控制按钮使用固定等宽列、统一 40×48 单元格、24×24 内容画布和布局取整；最小化横线使用显式居中矩形，避免细长 `StreamGeometry` 的 Uniform 拉伸贴到画布上沿；版本删除会清理只读属性，遇到短暂文件锁时先移出 `versions` 再后台重试；“跟随系统”现在读取 Avalonia 系统主题并监听后续切换 | `Controls/*.axaml`、`InstancesPageView.axaml`、`MainWindow.axaml`、`Resources/Theme.axaml`、`Services/ThemeService.cs`、`FeaturePagesViewModels` |

下文零散出现的 293/294/296/308/313 等测试数字均为各轮历史快照；当前验证结果以本段为准。
当前回归结果（2026-09-19 复验）：Avalonia Debug 构建通过，0 警告、0 错误；迁移测试 **356 总计，其中 352 通过、4 跳过**。上述整合包来源树、缓存、更新状态、备份回滚与透明布局回归均包含在本次测试集中。Windows 透明后端已确认接受 AcrylicBlur，最终无遮挡桌面截图及真实 Nexus/CurseForge 账号端到端流程仍属于发布前实机验收项。

本轮给 `build.ps1` 与 `scripts/package-avalonia.ps1` 增加可选 `-OutputDirectory`；未指定时仍输出到仓库 `artifacts`，指定后可安全地隔离构建。Windows x64 Debug 包装脚本已分别在 PowerShell 7 与 Windows PowerShell 5.1 中通过临时目录实跑，ZIP 包含主 EXE 和 PDB；根目录/仓库根目录会在发布前被拒绝，仓库现有调试 ZIP 的大小和时间戳保持不变。
上一轮测试统计（含 manual 来源、Modpack 游戏版本、半包下载重试、兄弟目录子 Mod 来源树、普通 Mod 安装来源树、加载期旧来源修复、CurseForge 整合包游戏版本详情、共享图片缓存兼容、Collection 子项进度刷新及详情分页回归、Nexus OAuth 头像 claim/旧缓存路径、API 限额快照、失效 Token 资料保留及限额事件订阅异常隔离回归）：迁移测试 **308 总计，其中 306 通过、2 跳过**。
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

本轮补齐启动器更新设置迁移：新增 `AutoDownloadUpdate` 的 Avalonia 持久化字段、WPF 旧配置导入和设置页开关；启动检查或手动检查发现更新时，可让更新弹窗自动下载发布资产，下载完成后仍由用户确认安装并重启。

本轮继续补齐启动器更新设置迁移：新增 `ShowUpdateNotification` 的 Avalonia 持久化字段、WPF 旧配置导入、设置页绑定和自动保存；启动时发现新版本时会按该开关决定是否显示通知，设置页手动检查仍可主动查看更新。

本轮完成旧 WPF/Core 工程编译收口：`SVL.Core` 使用 nullable 注解上下文消除历史 `CS8632` 噪声，修复延迟清理任务的未等待告警，并补上 SVL 整合包任务在 Premium 回退前触发 `NexusPremiumRequired` 通知事件；`SVL.sln` 当前 Debug 构建为 0 警告、0 错误。

本轮继续完成旧 WPF/Core 的 GitHub 更新链路：`ModManager` 会读取稳定 Release、过滤 draft/prerelease、选择非源码压缩包并记录版本与下载地址；`ModBatchUpdateTask` 已支持该直链的下载、安装和更新目标替换，旧入口不再把 GitHub UpdateKey 固定判定为“无更新”。

本轮还收口旧 WPF 的实例删除语义：`SettingsService.DeleteInstance` 不再逐个物理删除版本文件，而是在移除 Content/game junction 后将整个版本目录发送到系统回收站；只有回收站操作成功才更新实例列表，失败会保留原记录并返回错误。


另存为任务恢复时不再强制按 Mod manifest 校验最终文件；只要目标文件已完整落盘且不存在 `.part`/`.part.json`，即可跳过重复下载，仍在写入的半成品不会被误复用。

本轮继续补齐旧 WPF 设置迁移：`MaxConcurrentModLocalizationChecks` 已加入 Avalonia 配置、旧配置导入和设置页，并与 Mod 更新检测并发数分开使用；两项均限制在 1-16 个线程并有回归覆盖。

本轮继续补齐旧 WPF 搜索行为：`ShowModTypeFilterDisabledNotice` 已加入 Avalonia 配置、旧配置导入和设置页；Mod 搜索在来源为“全部”时会禁用类型筛选、清空已选类型，并按设置显示提示。
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
历史记录：此前构建曾报告 NuGet 的 `Tmds.DBus.Protocol 0.20.0` 安全公告；现已通过 Avalonia 项目中的显式版本约束升级到 `0.95.1`，重新还原后该警告消失。
旧 SMAPI 任务若在 `TargetGamePath` 中保存版本目录，真正调用安装器前也会再次归一化为 Base 路径，防止恢复任务产生嵌套 `versions`。
Windows Debug 单文件发布脚本已在本轮最终构建后重新执行通过；产物为
`artifacts/SVL_v1.2.0.0_debug_Windows_x64.zip`（压缩包包含单文件 EXE 及调试符号）。
迁移文档约定的 `scripts/package-avalonia.ps1` 兼容入口已补齐，并委托根目录
`build.ps1`，避免文档命令因脚本缺失而无法打包。
Unix 主机对应的 `scripts/package-avalonia.sh` 入口也已补齐，统一委托
`pwsh build.ps1`；默认构建全部目标，可通过 `PACKAGE_TARGETS` 或第二个参数指定
`windows`、`linux`、`macos`。Linux x64 现由统一入口发布 ZIP，并随包提供 `run-svl.sh`
以兼容 Windows 交叉打包时 ZIP 不保留 Unix 执行位的情况。

2026-09-15 后续补齐跨平台 NXM 注册：Linux 通过用户级 XDG desktop entry 和
`mimeapps.list` 注册；macOS 打包清单声明 `nxm` URL scheme，并通过 Launch Services
设置默认处理器。隔离目录回归覆盖 Linux 配置幂等/转义和 macOS bundle plist 解析；
Linux NXM 注册现覆盖多个桌面名优先级、失效候选回退和 MIME 关联校验，并新增通过真实
`NxmProtocolRegistrationService` 公共入口、隔离 XDG 环境变量注册桌面处理器的 Linux 集成回归，
且已在 Ubuntu WSL 实际运行通过。
Windows 本机全量迁移测试为 345 项（342 通过、3 个平台用例跳过）；Ubuntu WSL 全量测试为
345 项（344 通过、1 个 Windows 专用用例跳过），包含上述 Linux 服务入口集成回归，Avalonia Debug 构建 0 警告/错误，
Linux x64 Debug 与 Release 包均已在 Windows 主机交叉发布并验证 apphost 与 `run-svl.sh` 存在。
Linux/macOS 对应桌面环境的真实浏览器/游戏 scheme 跳转仍需桌面实机确认。
GitHub Actions 测试作业现扩展为 Windows、Ubuntu、macOS 三平台矩阵，并同时监听上游
`Dev-Avalonia` 和当前 fork 分支 `Avalonia-Dev`；发布作业也改为使用
统一脚本生成 Linux 启动归档与 macOS NXM `.app`/DMG，并加入归档/plist 断言。当前工作树尚未
推送，因此跨平台 CI 与 macOS 原生打包仍待 Actions 运行确认；Ubuntu WSL 测试已补足 Linux 用户态验证。
本轮 Windows 全量测试为 345 项（342 通过、3 平台跳过），Avalonia Debug 构建为 0 警告/错误；
Release Linux x64 ZIP 交叉发布成功，包含 `SVL.Avalonia` apphost 与 `run-svl.sh`。
README 的克隆命令现在显式检出 `Dev-Avalonia`；本地远端跟踪引用中该分支仍包含
`SVL.Avalonia/SVL.Avalonia.csproj`，避免用户默认克隆旧 WPF `main` 后找不到迁移工程。
最初 Ubuntu 全量测试发现一项重试测试在冷缓存下误触真实 SMAPI HTTPS 请求；现已让占位实例名带目标版本以模拟实际运行时版本元数据，避免机器缓存/外网依赖，Linux 和 Windows 全量测试均通过。Ubuntu WSL 已直接执行新增 XDG 服务集成用例；GitHub Actions 仍需推送后验证。

2026-09-16 追加原生 Linux 打包验收：Ubuntu WSL 安装经官方 SHA-256 校验的 PowerShell 7.6.6 用户级二进制后，实际运行 `build.ps1 -Config Release -Targets linux` 生成 Release ZIP；归档确认包含 `SVL.Avalonia` 与 `run-svl.sh`，apphost 为可执行 x86-64 ELF，启动脚本通过 `bash -n`。本机 Windows 迁移测试复验仍为 345 项（342 通过、3 跳过），Avalonia Debug 构建 0 警告/错误。PowerShell 来源：[官方 v7.6.6 Release](https://github.com/PowerShell/PowerShell/releases/tag/v7.6.6)。macOS 原生 `.app`/DMG 与推送后的 GitHub Actions 仍未验收。

同日在 Ubuntu WSL 使用打包脚本交叉发布 macOS Release：`osx-arm64` 与 `osx-x64` 均成功，产物 apphost 分别由 `file` 确认为 Mach-O arm64 与 Mach-O x86_64，且两个 publish ZIP 均包含 apphost。脚本在非 macOS 主机明确跳过 `.app`/DMG 组装，因此此项仅验证双架构交叉编译，不替代 macOS 原生包验收。

同日追加 Unix 用户入口验收：实际从 Ubuntu WSL 执行 `PACKAGE_TARGETS=linux bash scripts/package-avalonia.sh Release`，成功生成 Linux Release ZIP；归档包含 x86-64 ELF apphost 和 `run-svl.sh`。已从归档提取启动脚本并通过 `bash -n`，确认脚本会先为 apphost 设置执行权限再转发参数；ZIP 本身将条目记录为普通文件权限，因此 README 要求用 `sh run-svl.sh` 启动，不应直接假定归档保留 Unix 执行位。Linux Actions 归档验证步骤也新增启动器 Bash 语法检查，并已用 PowerShell 7 从真实 ZIP 执行同一检查通过。此项验证了文档中的 Unix 入口和实际包布局，仍不替代 GitHub Actions 与 macOS 原生 `.app`/DMG 验收。

2026-09-16 删除语义复核发现 Avalonia 共用的 `SmapiInstallService` 更新隔离实例时仍会物理删除旧 SMAPI 运行时文件和 `ConsoleCommands`/`SaveBackup`。现改为同卷暂存后整体调用共享回收站服务；若回收站拒绝，恢复原路径并中止更新，若回滚不完整则将暂存位置写入异常信息。用户 `Mods`（除 SMAPI 附带 Mod）和 `.svl-*` 元数据保持不动，junction/symlink 仅断开链接、不递归删除目标。新增应用回收目录、暂存回滚/成功回收及失败回滚四项测试；Windows 全量测试 349 项（345 通过、4 跳过），Ubuntu WSL 349 项（348 通过、1 跳过），Avalonia Debug 构建 0 警告/错误。

### 指定 Mod 的本机目录与缓存交叉核验（2026-09-15）

- 用户给出的 `Mods\AimonsWitchSwampOverhaulPatches` 目录当前不存在；该 Mod 实际
  安装目录为 `Mods\Distant Lands - Witch Swamp Overhaul`，manifest 的
  `UniqueID=AimonsWitchSwampOverhaulPatches`、版本 `2.2.9`。Avalonia 稳定缓存
  `cf-1010281-7942677.zip` 内有该父 Mod、CP 子 Mod 和 FTM 子 Mod，三个 manifest
  均为 `2.2.9`；父级来源为 CurseForge `1010281/7942677`，两个子目录均通过
  `parent-inherited` 指回该父目录，当前数据与压缩包结构一致。另有旧缓存别名
  `cf-1010281-7942.zip`，其 SHA-256 与正确 FileID 缓存完全相同；这是冗余历史缓存，
  本次只读核验未清理它。
- `Mods\[CP] CloneNPC_RSV` 存在，manifest 为 `d5a1lamdtd.MarketTown.CloneNPC_RSV`
  `5.0.0`，来源凭证为 `parent-inherited`，父路径 `[] MarketTown`。父 Mod 当前是
  `6.7.1`，来源 CurseForge `994458/8390242`。本机该文件缓存包含父 Mod 和另外四个
  ContentPack，但不含 CloneNPC；旧缓存 `994458/5276101` 则包含 CloneNPC RSV/SVE/
  Vanilla（均为 `5.0.0`）及旧版父 Mod `5.5.0`。
- 因此现存安装目录的父子来源标记正确，CloneNPC 不应独立拿父版本号提示“可更新”。
  本轮已补齐回导链：导出时在 `modpack.json`/`sources.json` 逐个记录嵌套子 Mod 的
  UniqueID、版本、父 Mod 关系与实际包含该版本的父归档 FileID；导入时先安装当前父
  Mod，再按稳定 ProjectID/FileID 获取历史父归档，只提取身份和版本均匹配的子 Mod，
  不会用历史归档覆盖/降级当前父 Mod。来源审计与提取均扫描任意嵌套层级；若本机
  有效的最新父归档明确不含子 Mod、又找不到历史缓存，则导出来源留空，避免写入错误
  FileID。新增回归测试覆盖历史归档选择、嵌套目标提取、父版本不降级和来源链写回。

## 仍需继续完善

1. 使用真实导出的 SVL/CurseForge/Nexus fixture 做一次端到端导入→安装→导出→再次导入，重点确认来源缺失、下载失败和旧包布局不会静默“安装完成”。当前已补本地直链端到端 fixture、导出包回导 fixture（覆盖自定义 Icon、配置覆盖、Nexus 缓存和 FileID）、真实 Nexus 文件页来源（`mods/29868?tab=files&nmm=1` + `File 7448774_...zip`）缓存命中、旧 WPF Nexus 缓存自动提升、直链来源保存/导出、来源缺失与缺失 `modpack.json` 的显式失败、设置目录按 manifest 映射、损坏/兼容 manifest、数字/字符串 FileID 兼容、无扩展名 7z、ZIP 越界防护、大小写不同的 manifest/Icon、Collection 镜像择一下载、Collection 外层目录下 `bundled` 解析、Collection 旧 source 字符串/URL 兼容，以及部分失败重试的更新模式回归；任务状态还会保存安装目录、速度、大小与子进度。近期又补充了生成式目录名（`cf-项目ID-文件ID`/`File 文件ID_...`）按 manifest 名整理、选中任务状态实时刷新、失败进度封顶 99%，CurseForge 页面/API 地址禁止直接下载、迁移标记允许新旧来源增量补迁移、迁移目标丢失后按旧来源恢复、CurseForge manifest 数字/字符串 ID、`files: null` 与 Collection 单字符串 `gameVersions` 的回归，以及“默认 SMAPI 图标可被包内 Icon 替换、用户自定义图标在重试时保留”的回归；`sources.json` 现兼容数组、常见对象包装、单来源对象、key→source 映射和 key→URL 简写，并可回退读取 `modpack.json.mods`，同时兼容 `site/provider/project/file` 等常见来源别名和真实 CurseForge CDN 路径回填 FileID；本地 SMAPI 复用会跳过损坏/无权限候选继续尝试可用实例；新增覆盖外层非目标 JSON 遮蔽内层有效 CurseForge/Collection 清单、未知 SMAPI 缓存拒绝复用、重复浏览器等待者和任务重试报告归属的回归。导出端还会从旧 Nexus 文件名/URL、CurseForge `cf-project-file` 目录名补回本地 FileID，减少不必要的线上查询；新增回归确认导入清单中的 NXM 一次性凭据会保留到解析请求；Mod 缓存复用现在必须通过有效 manifest 校验，延迟删除目录也会在启动/刷新时再次清理；SMAPI 默认图标写入会在资源流关闭后验证目标文件与实际解析路径，并兼容框架尚未初始化时的显式资源加载；SMAPI 官方回退兼容 `4.5.1.0` 到 `4.5.1` 这类 Release 标签差异；迁移还会在当前应用目录、工作目录与相邻 `SVL.Desktop`/WPF 目录内有限探测旧 `SVL/instances.json`，覆盖并排发布场景；Collection 稳定缓存命中现在覆盖直接安装入口，且 `.7z` 文件名不会再覆盖实际 ZIP 签名判断；本轮又补充了残留 source-only 目录过滤、Collection 顶层/嵌套来源字段归一化、bundled Mod 部分失败的逐项报告，以及管理页/导出页遇到单个无权限或重解析点目录时继续扫描其它 Mod 的安全遍历。
本轮将实际导出→SVL 回导→再次导入闭环改为真实 Content Patcher 来源身份（CurseForge ProjectID 309243、FileID 7448774），覆盖导出来源 URL/文件名、设置覆盖、有效 PNG 图标、损坏 bundled 条目显式失败和第二次导入缓存复用；对应测试通过。测试保留并恢复同 ID 的既有全局缓存，避免破坏用户缓存。
2. 搜索/详情/下载协议已有 fixture 覆盖；本轮新增并修复 Nexus 服务端关键词筛选无结果时的本地回退分页：按匹配结果应用 offset，逐批扫描原始 GraphQL 列表直到满足目标页或达到 400 条候选安全上限，避免 240 条后的深页丢失、后续页重复首屏；回归覆盖第 1–4 页及 HasMore。整合包分页已接入 CurseForge 服务端 index/pageSize，并让 Nexus 按目标页偏移增加候选拉取量；Nexus 页面仅有 Mod ID 时已接入 API/浏览器回退；兼容搜索入口已补齐筛选项与热门整合包首屏加载。下载页目录项和遗留搜索入口已保留结构化资源身份，详情展开/跳转优先使用结构化请求；详情下载 URL 会先剥离 `~~` 元数据，避免浏览器打开入口与安装入口行为不一致；CurseForge 解析层现在统一拒绝文件页/API URL，详情安装、批量更新、Modpack/Collection 安装共享同一安全边界；SMAPI 目录请求新增代理失败后的直连回退，并兼容 CurseForge 响应的大小写、`data/result/files/items` 多层包装及字符串 ID；SMAPI 目录给出网页地址时会回退到稳定 Forge CDN 路径；搜索/详情请求代次保护可避免旧响应覆盖新结果，来源未知或没有可安装文件时会显示明确提示；旧 SMAPI 任务若只保存版本路径或使用“SMAPI 版本 - 实例名”任务名，现在会恢复实例名并把目标路径归一化到 Base，避免下载后再次弹窗或生成嵌套 versions。仍需真实账号/网络完成 Nexus/CurseForge 的交互验收。
3. Windows 可见窗口冒烟已实际确认标题栏三个图形对齐、实例路径右键菜单可打开且为深色、图标选择弹窗为深色并可取消；最小化图形现改为居中矩形，Headless 测试断言其中心与 24×24 画布一致。主窗口现按本地资源为标题栏、面板、卡片和表面容器设置半透明，并让页面根 `UserControl` 保持透明；当前尚无无遮挡截图证明最终透底观感。Headless 125%/150% 缩放布局回归覆盖标题栏按钮尺寸、中心线和命中区域。不同实体 DPI、最大化/还原状态仍需额外验收。Icon 选择项、本地 Mod 详情、详情页图标/分隔线/加载遮罩、SMAPI 预发布标签和托管弹窗背景使用动态主题资源。
4. 旧 WPF/Core 工程的历史编译警告已清理；后续只需继续关注真实线上与可见 UI 验收。

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

本轮继续审计用户删除语义：旧 Core 的“修复嵌套 Mod 文件夹”在目标路径同名时，
不再直接删除原文件或原目录，而是先移入系统回收站；回收站失败会中止本次移动，
避免修复过程静默丢失已有内容。修复完成后对已搬空的嵌套目录做结构性清理仍为物理删除。

本轮继续收口旧 WPF 下载任务：普通 Mod 安装、就地更新和异常目标目录覆盖，
现在均会在覆盖前创建备份并将原路径移入系统回收站；仅保留下载压缩包、临时图标、
取消任务残留等生命周期清理的物理删除。

本轮来源判定收紧：`ContentPackFor` 只表示该 Mod 依赖 Content Patcher 等
框架，不能单独证明它是某个整合包归档中的子 Mod。整合包导入的独立条目
（包括 `sourceKind=modpack-entry`、仅带整合包 Project/File ID 的旧数据）
仍按 Mod 单独保留来源和更新状态；只有明确的 `parentMod`/`parent-inherited`
关系才继承父 Mod 来源并跳过子 Mod 的独立更新检查。这样可以修复实际
`[CP] CloneNPC_RSV` 被误判为“缺少来源/父级”的问题，同时保留真实复合
Mod 的父子树行为；对应独立条目、旧条目和显式父子关系均有回归测试。

本轮新增 Collection 任务详情回归：验证处理中条目会显示为当前 Mod，并在完成后清除当前标记；当前迁移测试为
**296 总计，其中 294 通过、2 跳过**。

本轮继续补齐 Collection `manual` 来源边界：任务详情新增“选择文件并安装”，
可将浏览器下载的 Mod 归档直接安装到该任务保存的实际实例目录；安装前仍执行
冲突比对与覆盖前备份，安装后按父 Mod/ContentPack 目录树写入来源凭证。

本轮继续收口 Collection 失败重试：显式重试会保留已成功条目的临时状态，
并由安装服务再次校验来源凭证或有效 `manifest.json` 后跳过；已安装条目不再
因其它 Mod 失败而重复下载/覆盖，残缺目录仍会回到正常安装流程。

本轮审计补充：`upstream/main` 在共同祖先之后仍只有 README 说明提交，没有新的
WPF/Core 业务功能；Avalonia 设置页已将实际共用的远程图片缓存从“SMAPI 图标缓存”
更正为“图片/图标缓存”，保留 `smapi-icon-cache` 目录名以兼容历史文件。复合来源
回归测试扩展为一个父 Mod 加六个 ContentPack 子 Mod，覆盖实际 `Blissful Valley`
的多子 Mod 形态。

本轮继续补齐旧图片缓存迁移：读取、统计、过期清理和全量清理同时兼容 WPF 的
`%LocalAppData%\\SVL\\cache\\images`；新下载仍写入 Avalonia 缓存目录，并保留旧版
按原始 URL 计算 SHA-256 的文件名规则，升级后无需重新下载已有远程图片。

本轮继续补齐 Collection 可选项决策：自动安装失败的可选 Mod 不再静默记为
Skipped，而是持久化为 NeedsDecision；任务详情提供“选择文件并安装”和
“跳过可选 Mod”，用户确认后的跳过列表会传入所有 Collection 安装/重试入口，
避免重试时再次下载已经明确跳过的条目。剩余必需 Mod 失败仍保持可重试状态。

本轮修复 Collection 子项的即时进度刷新：用户明确跳过可选 Mod 时统一经由
`SyncCollectionModProgress` 更新，而不是直接修改子项属性，确保任务详情的
“已处理数量/百分比”与持久化状态同步更新。

本轮继续收口 Collection 父子状态联动：任务会订阅逐 Mod 子项及集合本身的变化，
即使补装、跳过或后续入口直接修改子项，也会同步刷新任务汇总进度、当前条目和
任务详情绑定；清空/重建子项时会解除旧订阅，避免重复通知。

本轮补齐旧 WPF 的 `LauncherVisibility` 行为：Avalonia 设置页可选择五种启动器
可见性策略，旧 `app.json` 中的数值枚举和常见字符串枚举会迁移；实际启动时由主窗口
响应游戏进程启动/退出事件，支持隐藏后恢复或关闭，且不把旧 net48 工程直接引用到
Avalonia net10 工程。

本轮补齐旧 WPF 的 `FontSize` 设置：保留 12/13/14/15/16/18/20 选项，旧配置迁移
后绑定到 Avalonia 主窗口的字体继承链，设置页修改会自动保存并即时更新；无效值回退
到 14，避免配置损坏导致界面失去字体大小选择。

本轮补齐旧 WPF 的 `EnableTransparency` 设置：旧配置会迁移到 Avalonia，设置页修改会
自动保存并即时应用；主题资源使用半透明窗口背景，主窗口请求 Acrylic/Transparent
级别，不支持透明的平台仍保持不透明回退。

本轮对齐旧 WPF 的整合包并发下载语义：`MaxConcurrentModDownloads` 迁移到
`CollectionDownloadParallelism` 后保留默认值 3 和 1–10 的有效范围，设置页、任务队列
及整合包安装器使用同一上限，并新增 10 线程配置的迁移回归。

本轮迁移 WPF Nexus OAuth 头像与 API 限额卡片：读取 ID Token 的 `picture`/兼容字段，
保留旧头像缓存目录与命名规则，设置页优先使用本地缓存并在后台补充下载；头像缓存纳入
图片/图标缓存的统计、过期清理和全量清理。新增统一限额快照，目录 GraphQL/文件请求、
NXM 地址解析和登录验证均记录 `X-RL-*` 响应头，设置页显示小时/每日已用量，退出登录时
清空快照；快照通知已隔离订阅者异常，后台请求不会因 UI 订阅生命周期问题被误判失败。

本轮并发安全修复：整合包/Collection 安装完成时，实例注册表改用进程内原子
Upsert，避免并行任务的读-改-写互相覆盖；新增 32 个并发实例写入回归测试。

本轮来源写入修复：旧 Core 在复合归档根目录自身包含 `manifest.json` 时，不再把父
目录再次作为 ContentPack 写入，避免父 Mod 的来源凭据被覆盖成指向自身的
 `parentMod`；后续更新、导出和回滚可以继续识别父级来源。旧 Core 本地构建通过，
Avalonia 迁移回归测试为 **313 总计，其中 311 通过、2 跳过**。

本轮继续补齐整合包内置 Mod 的来源归属：SVL/CurseForge 整合包中随包解压或从
`overrides/Mods` 覆盖的 Mod，现在写入 `sourceKind=modpack-bundled` 及整合包名称/
版本；该标记不包含独立 ProjectID/FileID，因此不会触发错误的更新检查。随同一归档
安装的父 Mod 与 ContentPack 会复用统一来源树，子 Mod 写入 `parent-inherited`，
已有独立来源不会被覆盖；管理页显示“整合包内置”而非“缺少来源信息”。

本轮补齐设置迁移：旧 WPF 的 `EnableAnimations` 已接入 Avalonia 设置页与运行时，
通过动态过渡资源即时关闭/恢复已有页面动画，并新增 Headless 回归断言。

本轮继续审计旧 WPF Collection 安装器：内置 Bundled Mod 和单根目录归档的完整替换
仍会先创建 `ModsBackup` 备份，再将原目录移入系统回收站；而多文件 `bundled` 合并
与 `patches` 部分覆盖会保留当前目录，并在任何覆盖前创建完整 `ModsBackup` 快照，
避免把未被补丁包含的文件一起移走。缓存、临时解压目录和失败任务残留仍按生命周期
清理，不纳入用户内容保护范围。

当前 Avalonia Collection 安装器的 `patches` 路径也已统一复用 `DownloadInstallService`
的备份实现：当缓存命中已存在的 Mod 并准备部分覆盖时，先创建 `ModsBackup` 快照；
备份失败则跳过该补丁并保留原目录，不再直接改写用户文件。

本轮再收口 Avalonia 整合包安装事务：替换已安装 Mod 后，事务暂存的旧目录现在
必须进入系统回收站；回收站失败会恢复旧目录并让更新失败，避免旧版本随着 `_staging`
清理被物理删除。

本轮继续收口旧 WPF SMAPI 隔离实例清理：更新时移除附带 Mod、生成文件和非 Mods
生成目录，以及失败/取消时本次创建的版本目录，均改为移入系统回收站；`Content`
junction 仍只执行结构性断开，下载缓存和临时目录继续按生命周期物理清理。

本轮继续收口失败/取消清理：Avalonia 的 SVL、CurseForge、Nexus Collection 新建版本
目录，以及旧 Core 的 SVL/CurseForge 导入任务版本目录，不再通过 `Directory.Delete`
物理删除；删除版本遇到文件锁后进入 `.svl-delete-*` 队列的目录，也会继续重试移入
回收站，失败则保留待后续处理。缓存、临时归档、事务暂存和已搬空的 junction 仍属于
生命周期/结构清理，不纳入用户内容回收范围。

本轮 Windows 可见 UI 复核：使用调试构建和 `SmokeInstance` 只读查看启动页、Mod 管理及实例路径列表；确认实例路径右键菜单可打开，菜单和图标选择弹窗均按深色主题渲染。实际截图发现最小化横线仍因细长 `StreamGeometry` 的 Uniform 布局落在画布上部；已改为 12×2 DIP 居中矩形，并在 Headless 布局测试中断言其中心 Y=12。弹窗通过“取消”关闭，未保存图标或个性化修改。随后独立启动 Windows Debug 主窗口，实际日志报告 `actual=AcrylicBlur`，故已确认平台接受 AcrylicBlur 级别；当时桌面上层窗口遮挡了应用截图，不能据此判断透底观感。真实视觉对照及不同实体 DPI/窗口状态下的命中区域仍待单独确认。

本轮任务失败建议分类收紧：只有“无法解析下载地址”才作为来源缺失/来源解析问题显示补充来源建议，普通 Collection manifest 解析失败保留通用错误建议；新增两条任务状态回归。Nexus 本地关键词回退分页也新增回归，修复 offset 被忽略造成翻页重复的问题，并覆盖搜索匹配项位于 240 条原始候选之后的第 3、4 页；以上均已包含在当前 334 条测试统计中。

本轮继续修复 Modpack 来源归属：父条目残留的 `childMods` 不能覆盖清单中具有不同稳定来源身份的独立 Mod 条目；同一平台/项目但 FileID 不同仍按独立条目写入。来源修复只按平台、项目、FileID 精确合并；仅对没有来源凭证的旧子目录，才按 manifest 关系补写 `parent-inherited`。同归档父子 Mod 继续继承父来源，管理页也不会把具有独立来源凭证的条目误分组或误判为继承来源。

本轮收口资源详情的结构化身份：下载目录的 Mod、Modpack、SMAPI 搜索结果、分页缓存及详情路由直接传递 `ModSearchResultItem`/`CatalogResourceIdentity`，移除了展示字符串往返解析；来源原文与社区汉化名称/摘要分别保留，语言切换不受展示文本分隔符影响。版本设置页的在线 Mod 详情也改为直接发送结构化身份，详情页移除了旧 displayText 解析器；缺少项目 ID 时不再请求伪详情，仍打开来源搜索页。新增来源身份、Collection slug、双语名称/摘要、分隔符摘要及版本设置来源 ID 回归。

本轮 Windows 只读复验（2026-09-15）：启动当前 Debug 构建，在个性化页确认主题模式为“跟随系统”、透明效果开关保持启用；最大化与还原按钮均成功命中，截图中三个标题栏按钮处于同一行。新增透明开关资源 Alpha 与 Acrylic/Transparent/None 窗口提示回归；另新增 Headless 125%/150% 渲染缩放布局回归，断言三个按钮尺寸/中心线/独立命中区域稳定，最小化图形中心偏差不超过半个 DIP。未切换/保存设置。后续独立 Debug 主窗口实测日志返回 `actual=AcrylicBlur`，确认 Windows Avalonia 后端实际提供该级别；由于截图被上层桌面窗口覆盖，视觉透底效果仍未验证，Headless 缩放也不替代不同实体显示器/DPI 的实机命中测试。

2026-09-16 继续处理“Debug 可透底、主页面不明显”：定位到主窗口的大部分内容使用不透明 `CardBrush`/`SurfaceBrush`，且页面 `UserControl` 再叠加一层背景，遮住了 Acrylic。现将主窗口背景、标题栏、面板、卡片和表面画刷改为主窗口级资源，页面根 `UserControl` 保持透明；半透明后端不支持时恢复不透明，其他窗口继续使用原全局画刷。Headless 回归已挂载实际页面与卡片，确认页面根控件透明、卡片解析到局部半透明画刷，并验证主题颜色变更仍保留 Alpha、全局弹窗画刷不变。Windows 全量迁移测试 349 项（345 通过、4 跳过），Ubuntu WSL 349 项（348 通过、1 跳过），Avalonia Debug 构建 0 警告/错误。尝试可见窗口复核时 Computer Use 运行时初始化连续两次失败，故真实桌面透底观感仍需后续截图验收。

2026-09-19 重新抓取上游 refs：`upstream/main=19ef4ef` 在共同祖先之后仍只有 README 说明提交；`upstream/Dev-Avalonia` 与 `upstream/Avalonia-Dev=a5f9669`，没有发现当前 Avalonia 工作树遗漏的上游业务提交。通过已配置的 CurseForge MCP 及只读文件列表接口复核实际安装来源：Distant Lands 项目 `1010281` 的最新 FileID 为 `7942677`，Market Town `994458` 为 `8390242`，More Accessories `1012214` 为 `5380939`，Content Patcher `309243` 为 `7759981`；均与本地 `svl-source.json` 一致。整合包父 Mod 与嵌套子 Mod 的来源关系未发现错绑。

2026-09-19 继续修复主窗口透明时序：`MainWindow` 不再在 `Opened` 或
`ActualTransparencyLevel` 暂时报告 `None` 时把用户已开启的半透明画刷恢复为不透明；
平台不支持透明时仍由 `TransparencyBackgroundFallback` 提供不透明回退。新增已打开窗口
模拟该时序的回归断言。当前 Avalonia Debug 构建 0 警告/错误，迁移测试 349 总计、345
通过、4 跳过；最终无遮挡桌面透底观感仍需真实窗口截图验收。

2026-09-19 删除语义审计又发现旧 Nexus Collection 向导的取消安装分支仍会物理删除
新建版本目录。现改为先断开版本内的 `Content`/`game` junction，再将版本目录移入
系统回收站；回收失败时保留目录并记录警告。Collection 解压目录和临时 SMAPI 包仍
按临时生命周期清理。`SVL.Core` 与 `SVL.Desktop` Debug 构建均通过，0 警告、0 错误。

随后重新抓取上游 refs：`upstream/main=19ef4ef` 在共同祖先之后仍只有 README 说明提交，
`upstream/Dev-Avalonia=a5f9669` 没有当前工作树遗漏的提交；本轮提交 `5676700` 已快进
到 `upstream/Avalonia-Dev`，因此当前上游 Avalonia 分支与本工作树一致。

2026-09-19 继续收口备份恢复事务：恢复前创建的原 Mod 快照现在会被精确记录并作为
回滚源；如果原目录已经移入回收站但新目录发布或更新链清理失败，会先将新目录移入
回收站，再从快照恢复原目录。回滚仍不物理删除用户内容，回滚失败时保留
`ModsBackup` 快照供备份栏手动恢复。新增回滚回归测试；当前迁移测试 **350 总计、346
通过、4 跳过**，Avalonia Debug 构建 0 警告/错误。

2026-09-19 补齐已迁移设置中的系统托盘运行时行为：Avalonia 现在会注册托盘图标，
提供“显示主窗口”和“退出”菜单；设置“启动时最小化到托盘”会在窗口句柄创建后隐藏
主窗口，设置“关闭时最小化到托盘”会拦截用户关闭并隐藏窗口。托盘不可用的后端会安全
回退到正常窗口；启动游戏、托盘退出、应用 Shutdown 和系统关机均绕过拦截，不会因为
托盘设置阻止真正退出。收到 NXM 或托盘显示请求时，隐藏的主窗口会重新显示并激活。
新增 Headless 回归测试覆盖“用户关闭隐藏、显式退出放行”；当前迁移测试 **351 总计、
347 通过、4 跳过**，Avalonia Debug 构建 0 警告/错误。Windows/macOS/Linux 实机托盘
图标与不同桌面环境的原生菜单仍需最终验收。

2026-09-19 继续收口深浅色主题：整合包检测失败、实例名校验错误、下载悬浮按钮徽标
及浮窗通知不再使用固定颜色；通知成功/错误/警告/信息背景和前景色已进入主题资源，
深色模式使用独立的深色底色保证白色文字对比度。新增浅色/深色通知资源回归断言；当前
迁移测试 **352 总计、348 通过、4 跳过**，Avalonia Debug 构建 0 警告/错误。

2026-09-19 继续收口本地 Mod 详情弹窗的主题适配：启用/禁用状态标签不再直接写死
背景色与白色文字，改为 `ModStatusEnabledBackground`、
`ModStatusDisabledBackground` 和 `TextPrimaryBrush`，深浅色切换会同步更新；启用
状态改为显式的 `IsModEnabled`/`IsModDisabled` 绑定，避免用颜色字符串承担状态语义。
新增浅色/深色资源回归断言；当前迁移测试仍为 **352 总计、348 通过、4 跳过**，
Avalonia Debug 构建 0 警告/错误。

2026-09-19 再次抓取上游 refs：`upstream/main=19ef4ef` 相对共同祖先仍只有
`README.md` 说明性提交；`upstream/Dev-Avalonia=a5f9669` 没有当前工作树之外的
提交，`upstream/Avalonia-Dev=583f3e8` 已与当前工作树一致，因此没有发现新的
待迁移业务代码。通过已配置的 CurseForge MCP 只读核对了 `Market Town`（项目
994458）、`More Accessories`（项目 1012214）、`Content Patcher`（项目 309243）
和 `Cape Stardew`（项目 995972）的来源身份；这次核对不改变应用缓存或远端数据。
Nexus MCP 已配置但当前开发环境尚未提供 API Key/Cookie，真实 Nexus 账号验收仍
保持为发布前检查项。

2026-09-19 继续修复远端 Mod 更新判定：CurseForge 文件列表现在兼容 `data`、
`result/files` 等嵌套响应，并按发布时间优先、FileID 同时间兜底选择最新发布文件，
不再简单取最大 FileID。Nexus/CurseForge 均改为“稳定 FileID 优先”：本地已记录
FileID 时，即使新包的 `manifest.json` 仍保留旧版本，只要远端发布文件身份变化就提示
一次更新；安装完成后 FileID 相同不会再次恢复更新状态；历史来源没有 FileID 时仍要求
版本文本明确变新。新增 2 条回归测试；当前迁移测试 **354 总计、350 通过、4 跳过**，
Avalonia Debug 构建 0 警告/错误。提交 `28cc881` 已推送到上游 `Avalonia-Dev` 和
`codex/avalonia-dev-20260919`，Actions `35446022450` 的 Windows、Linux、macOS
构建测试及三平台打包全部成功。

2026-09-19 收口旧 Core `ModDownloadTask` 的取消/失败清理：解压过程中已经写入用户
`Mods` 目录的目标路径现在统一移入系统回收站；如果回收站调用失败则保留原路径，
不再回退为不可恢复的物理删除。下载缓存、临时解压根目录及其它不属于用户内容的
临时路径仍按生命周期物理清理。`SVL.Core` net48 Debug 构建通过；该旧 WPF/Core
入口不属于 Avalonia 测试项目，行为验证以兼容性构建和现有安装/更新回归覆盖为准。

2026-09-19 继续收口删除语义：Avalonia `SmapiInstallService` 新装 SMAPI 失败/取消时，
版本目录改为移入统一回收站服务；回收站失败会保留半成品目录。旧 WPF Collection
向导清理空版本目录、旧 Core ModManager 整理空嵌套目录，以及 Avalonia 在线更新备份
异常路径也不再直接物理删除用户目录。新增 SMAPI 版本目录“移入回收站/回收失败保留
现场”回归；当前迁移测试 **356 总计、352 通过、4 跳过**，Avalonia、旧 WPF Debug
构建均为 0 警告/错误。

本轮补齐旧 WPF Nexus Collection 向导最后两个用户内容删除点：单根/无根模式解压时
覆盖 Mods 内已有文件，以及安装成功后清理复制到 Mods 目录的 ZIP，现在均先移入系统
回收站；回收失败时保留文件，避免静默丢失。多根模式的临时解压目录仍按临时数据
生命周期清理。

本轮继续处理主页面透底不明显：主窗口局部资源现在使用更低的 Alpha
（Window 0x66、Header 0x99、Panel 0x33、Card/Surface 0xA6），而 Debug/确认等独立
窗口仍使用应用级不透明卡片资源；透明度回归测试同步更新，避免把主窗口作用域的
透明画刷泄漏到其它窗口。最终桌面无遮挡观感仍需 Windows 实机截图验收。

本轮补齐旧 WPF Nexus Collection 向导的 manual 条目处理：带明确归档直链的条目进入
直链下载与安装队列；没有归档直链的必需条目记为失败、可选条目记为跳过并保留原因，
不再静默完成。即使 Collection 没有 Nexus 自动下载项，也会继续执行 bundled、patches
和实例配置阶段；旧 WPF Debug 构建通过。

2026-09-20 继续修复主窗口透明级别选择：主窗口现在优先请求 `AcrylicBlur`，再回退到
`Transparent`；此前 `Transparent` 排在前面时，Windows 会在它可用时直接选中纯透明级别，
导致 Acrylic 材质不会生效、主页面视觉上不明显。透明回归测试同步更新；本地迁移测试
356 总计、352 通过、4 跳过，Avalonia 与旧 Core Debug 构建均为 0 警告/错误。

同日收紧旧 Nexus Collection 来源类型解析：对外部 `source.type` 先执行 Trim 和
`ToLowerInvariant`，兼容生成器产生的大小写差异和首尾空格，避免合法的 browse、bundle、
manual、direct 条目被归入未知来源而静默跳过。
