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
