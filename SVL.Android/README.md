# SVL Android 独立版开发预览

目标是在 Android 上由 SVL 独立完成游戏文件获取、SMAPI 与 Mod 管理及游戏启动，不要求安装其他启动器，也不分发游戏文件。

当前预览版可以选择正版 PC 游戏的 ZIP，检查游戏程序集和资源目录，将文件导入应用私有的独立实例。导入过程不会修改源文件或已有实例，并会拒绝越界路径、重复路径和符号链接。检查仅确认关键文件存在，不代表游戏完整或可以运行。

**尚未实现：Steam 登录和下载、Android 游戏运行环境、SMAPI 安装、Mod 和整合包管理，因此当前预览版不能启动游戏。**这些功能需要在真机上逐项验收后才能视为完成。

## 后续技术路线

1. 在 Android 内实现 Steam 登录、所有权校验、分块下载及断点恢复。可研究 [SteamKit](https://github.com/SteamRE/SteamKit) 和 [Cinderbox Companion](https://github.com/ObfuscatedVoid/Cinderbox-Companion)，不得记录密码或在日志中泄露凭据。
2. 研究独立运行 PC 游戏程序集所需的 Android Mono/.NET 宿主、[MonoGame 上游源码](https://github.com/MonoGame/MonoGame)、图形、音频和输入桥接。Cinderbox 主运行器源码尚未公开，其 [MonoGame 分支](https://github.com/Ekyso/MonoGame)目前也仅有说明和许可证，不能当作完整源码直接集成。
3. 按目标运行环境适配 [SMAPI Android 分支](https://github.com/Ekyso/SMAPI-For-Cinderbox)，并处理 Mod 兼容性、权限、崩溃日志和实例隔离；桌面版 SMAPI 不能直接当作 Android 运行时使用。
4. 引入和分发第三方代码或二进制前，逐项核实许可证和对应源码义务。SteamKit、SMAPI 等组件各自有独立许可证；[JunimoGate](https://github.com/leontismaro/JunimoGate) 的启动器代码为 GPL-3.0-only，当前没有复制或集成其代码。
5. 完成单元测试、ARM64 真机、Steam 登录下载、原版和带 Mod 的实际启动验证，再考虑正式发布。

## 构建

移动项目使用 Avalonia 11.3.12，修复旧版本 Android 构建时可能重复拼接程序集路径的问题；SkiaSharp 3 的 Android 原生库支持 16 KB 内存页对齐。桌面项目不受影响。需要 .NET 10 Android workload、Android SDK 和 JDK：

```powershell
dotnet workload install android
dotnet publish SVL.Android/SVL.Android.csproj -c Release -f net10.0-android -p:RuntimeIdentifier=android-arm64 -p:AndroidPackageFormat=apk
```

默认签名仅供开发测试；正式分发需要使用项目自己的发布签名。x86_64 模拟器的 ARM 转译不等同于 ARM64 真机验收。
