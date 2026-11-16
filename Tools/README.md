# Windows 发布

在 Windows 安装 .NET 10 SDK 后，从仓库根目录运行：

```powershell
./Tools/Publish-Windows.ps1
```

默认生成自包含 Windows x64 程序和 ZIP，接收者解压后运行 `MajMusic.exe`。

目标电脑已有 .NET 10 x64 运行时时，可生成更小的版本：

```powershell
./Tools/Publish-Windows.ps1 -Mode FrameworkDependent
```

每次发布在 `artifacts` 下创建独立目录；分发生成的 ZIP 或完整 `app` 文件夹即可。脚本只包含 Windows x64 依赖，保留完整 LibVLC 插件，排除调试符号和本地运行数据，并生成大小清单。成功后自动清理本次中间产物，`-KeepBuild` 可保留它们用于排查。

# Android ARM64 发布

准备 .NET 10 SDK、Android workload、Android SDK 36 与 JDK 17 或 21：

```powershell
dotnet workload install android
./Tools/Publish-Android.ps1
```

脚本自动查找工具，也可通过 `-DotNet`、`-AndroidSdkDirectory` 和 `-JavaSdkDirectory` 指定位置。生成的 `artifacts/android-arm64-*/MajMusic-android-arm64.apk` 使用 Android SDK 标准开发密钥签名，仅包含 `arm64-v8a`。脚本检查签名与包对齐，不会自动安装到设备。

# Android 冷启动验证

显式指定已授权调试设备，测试其已安装版本：

```powershell
./Tools/Test-AndroidStartup.ps1 -Serial <adb-device-serial>
```

使用 `-ApkPath <apk文件>` 可先覆盖安装包名为 `com.icecloud.majmusic` 的 APK。脚本连续冷启动三次，每次等待五秒，核对进程仍存活、前台 Activity 属于本应用，以及对应 PID 的日志没有致命异常。每次启动的结果与日志保存在 `artifacts/android-startup-*`。
