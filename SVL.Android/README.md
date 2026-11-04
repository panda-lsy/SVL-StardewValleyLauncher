# SVL Android 开发预览

这是 Android 适配的第一步，使用 Avalonia 11.3.12、桌面版启动器图标，并引用 `SVL.Core.Platform`。移动项目使用较新的 Avalonia 补丁版本，以修复旧版本在 Android 构建时可能重复拼接程序集路径的问题；另使用 SkiaSharp 3 的 Android 原生库，适配 16 KB 内存页设备。桌面项目不受影响。现阶段可以检测并打开已安装的 Cinderbox，以及打开其发布页和使用说明。

Cinderbox 是独立的第三方 Android 运行器。玩家在其中选择导入自己已有的 PC 游戏压缩包，或使用运行器提供的 Steam 登录和游戏下载，再由运行器启动游戏。SVL 不接收 Steam 凭据，也不捆绑 Cinderbox APK 或游戏文件；SVL 自身的 Steam 下载、Mod 管理、整合包和游戏内桥接尚未接入。Cinderbox 0.8.1 的 APK 声明 `arm64-v8a`；x86_64 模拟器需要支持 ARM 转译，也不能代替 ARM64 真机验收。

参考项目：

- [Cinderbox](https://github.com/Ekyso/Cinderbox)：官方发布页提供 APK，但截至本次检查，其公开 Git 仓库只有说明与许可证，没有可供集成的运行器源码。0.8.1 的公开启动包名为 `com.game.cinderbox`。
- [Cinderbox Companion](https://github.com/ObfuscatedVoid/Cinderbox-Companion)：MIT 许可证，公开了使用 JavaSteam 登录和下载游戏的实现，可用于后续研究；此预览版未复制或集成其代码。
- [Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)：用户提供的 SteamPipe 下载流程参考；其 Godot 游戏兼容层不能直接用于基于 MonoGame 的星露谷物语。
- [JunimoGate](https://github.com/leontismaro/JunimoGate)：面向官方 Android 游戏的 SMAPI 实现，GPL-3.0-only；该项目与当前 PC 游戏运行方案不同，本预览版未引入其代码。

构建需要 .NET 10 Android workload、Android SDK 和 JDK。例如：

```powershell
dotnet workload install android
dotnet publish SVL.Android/SVL.Android.csproj -c Release -f net10.0-android -p:RuntimeIdentifier=android-arm64 -p:AndroidPackageFormat=apk
```

默认生成的签名仅供开发测试；正式分发需要使用项目自己的发布签名。ARM64 预览包已经在支持 ARM 转译的 x86_64 模拟器中完成启动和运行器识别验证，仍需在 Android ARM64 真机上验证运行器发现与启动、Steam 授权和实际下载、游戏导入及启动，并继续设计可复用的 Steam 下载服务与移动端 Mod 管理。不要把桌面的 SteamCMD 或 Windows/Linux 可执行文件当作 Android 原生程序运行。
