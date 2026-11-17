# Windows 发布

在 Windows 安装 .NET 10 SDK 后，从仓库根目录运行：

```powershell
./Tools/Publish-Windows.ps1
```

生成内置 .NET 10 和 MajView 的 Windows x64 程序和 ZIP，接收者解压后运行 `MajMusic.exe`。

每次发布在 `artifacts` 下创建独立目录；分发生成的 ZIP 或完整 `app` 文件夹即可。脚本只包含 Windows x64 依赖，保留完整 LibVLC 插件，排除调试符号和本地运行数据，并生成大小清单。成功后自动清理本次中间产物，`-KeepBuild` 可保留它们用于排查。

# Android ARM64 发布

准备 .NET 10 SDK、Android workload、Android SDK 36 与 JDK 17 或 21：

```powershell
dotnet workload install android
./Tools/Publish-Android.ps1
```

脚本自动查找工具，也可通过 `-DotNet`、`-AndroidSdkDirectory` 和 `-JavaSdkDirectory` 指定位置。生成的 `artifacts/android-arm64-*/MajMusic-android-arm64.apk` 使用 Android SDK 标准开发密钥签名，仅包含 `arm64-v8a`，内置 .NET 10 和 MajView。脚本检查签名与包对齐。

# MajView 构建资源

发布脚本会准备并校验 MajView 渲染器，存入 `.work/majview-runtime` 重复使用。使用 Rider 调试预览前可运行 `./Tools/Prepare-MajViewRuntime.ps1`；已有一套相同版本的渲染文件时，可以传入 `-SourceDirectory <目录>`。

定制版构建输出到 `.work/majview-runtime-custom`，含四个 `Build.*` 文件与 `runtime.json`。发布脚本优先使用这个目录，也接受 `-MajViewDirectory <目录>` 指定经校验的构建。

在 Unity Hub 为 6000.3.17f1 安装 WebGL Build Support，然后从仓库根目录构建定制版：

```powershell
./Tools/Build-MajViewRuntime.ps1 -UnityEditor 'C:\Program Files\Unity\Hub\Editor\6000.3.17f1\Editor\Unity.exe'
```

脚本在 `.work` 检出固定版本的 MajView 工程，应用 `Tools/MajViewEmbedded.patch`，然后生成两个发布脚本共同使用的渲染器文件。定制发行包的 `ThirdPartyNotices/MajViewSource` 附带这两个文件。

# Android 冷启动验证

显式指定已授权调试设备，测试其已安装版本：

```powershell
./Tools/Test-AndroidStartup.ps1 -Serial <adb-device-serial>
```

使用 `-ApkPath <apk文件>` 可先覆盖安装包名为 `com.icecloud.majmusic` 的 APK。脚本连续冷启动三次，每次等待五秒，核对进程仍存活、前台 Activity 属于本应用，以及对应 PID 的日志没有致命异常。每次启动的结果与日志保存在 `artifacts/android-startup-*`。

# Android 原生凭据验证

```powershell
./Tools/Test-AndroidCredentials.ps1 -Serial <adb-device-serial>
```

脚本自动查找 .NET SDK、Android SDK 与兼容的 Android Studio、Unity 或独立 JDK，构建并安装独立测试包 `com.icecloud.majmusic.tests`。测试使用随机值检查写入、读取、跨实例保留、更新和删除，共 12 项原生 Keystore 断言；完成后卸载测试包。日志保存在 `artifacts/android-credentials-*`。

`-CredentialStoreSource <旧版源码路径>` 可链接旧实现做失败对照，`-KeepInstalled` 保留测试包用于检查。工具位置可通过 `-DotNet`、`-AndroidSdkDirectory` 和 `-JavaSdkDirectory` 指定。

# Windows 切页测量

[NativeNavigation](Diagnostics/NativeNavigation/README.md) 使用生成曲库和独立 Windows 窗口，记录切页调用、首次布局、界面帧回调及调度间隔，自动输出报告并关闭窗口。每次构建使用独立输出目录，可对照页面预热、卡片复用与切换前后的耗时。
