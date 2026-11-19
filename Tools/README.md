# GitHub 发布

打开 **Actions → Release MajMusic → Run workflow**，填写 `version`（例如 `0.1.0`）。构建 Windows x64、Android ARMv7 和 Android ARM64。启用 `publish` 时，全部包通过后发布到同一个 `v<version>` Release。关闭 `publish` 可只生成 Actions 测试产物。

正式 APK 使用同一套 Android 签名。在仓库的 Actions Secrets 配置 `ANDROID_KEYSTORE_BASE64`、`ANDROID_KEY_ALIAS`、`ANDROID_STORE_PASSWORD` 和 `ANDROID_KEY_PASSWORD`。

# Windows 发布

在 Windows 安装 .NET 10 SDK 后，从仓库根目录运行：

```powershell
./Tools/Publish-Windows.ps1
```

生成内置 .NET 10 和 MajView 的 Windows x64 程序和 ZIP，接收者解压后运行 `MajMusic.exe`。

每次发布在 `artifacts` 下创建独立目录；分发生成的 ZIP 或完整 `app` 文件夹即可。脚本只包含 Windows x64 依赖，保留完整 LibVLC 插件，排除调试符号和本地运行数据，并生成大小清单。成功后自动清理本次中间产物，`-KeepBuild` 可保留它们用于排查。

# Android 发布

准备 .NET 10 SDK、Android workload、Android SDK 36 与 JDK 17 或 21：

```powershell
dotnet workload install android
./Tools/Publish-Android.ps1
```

脚本默认生成 ARM64 APK，传入 `-RuntimeIdentifier android-arm` 可生成 ARMv7 APK。两种 APK 都内置 .NET 10 和 MajView，脚本会检查签名和对齐。

# MajView 构建资源

发布脚本校验仓库内的 MajView 构建压缩包，并将渲染器内置到 MajMusic 应用。使用 Rider 调试预览前可运行 `./Tools/Prepare-MajViewRuntime.ps1`。

定制版构建输出到 `.work/majview-runtime-custom`，含四个 `Build.*` 文件与 `runtime.json`。发布脚本优先使用这个目录，也接受 `-MajViewDirectory <目录>` 指定经校验的构建。

在 Unity Hub 为 6000.3.17f1 安装 WebGL Build Support，然后从仓库根目录构建定制版：

```powershell
./Tools/Build-MajViewRuntime.ps1 -UnityEditor 'C:\Program Files\Unity\Hub\Editor\6000.3.17f1\Editor\Unity.exe'
```

脚本在 `.work` 检出固定版本的 MajView 工程，应用 `Tools/MajViewEmbedded.patch`，然后生成两个发布脚本共同使用的渲染器文件。定制发行包的 `ThirdPartyNotices/MajViewSource` 附带这两个文件。

# MajView 音画同步回归

在仓库根目录运行下面的检查。需要 Node.js、Playwright 的 `playwright-core`、Chromium，以及支持 `drawtext` 的 FFmpeg。`PLAYWRIGHT_MODULE` 可以指向已安装的 `playwright-core/index.mjs`，`CHROMIUM` 可以指定浏览器程序。

```powershell
./Tools/Prepare-MajViewRuntime.ps1 -ReleaseRuntime
./Tools/Diagnostics/MajViewSync/Make-Fixture.ps1 -Ffmpeg ffmpeg
node Tools/Diagnostics/MajViewSync/check.mjs .work/majview-sync-fixture
```

测试曲每半秒响一拍，PV 带时间码和独立音轨。检查直接读取浏览器实际播放的音频与视频，在桌面和触屏布局下覆盖 0.25 至 2 倍速、播放中定位、暂停、旋转和曲末，并检查 PV 音轨保持静音。

# Android 冷启动验证

显式指定已授权调试设备，测试其已安装版本：

```powershell
./Tools/Test-AndroidStartup.ps1 -Serial <adb-device-serial>
```

使用 `-ApkPath <apk文件>` 可先覆盖安装包名为 `com.icecloud.majmusic` 的 APK。脚本连续冷启动三次，每次等待五秒，核对进程仍存活、前台 Activity 属于本应用，以及对应 PID 的日志没有致命异常。每次启动的结果与日志保存在 `artifacts/android-startup-*`。

Android 有共享存储访问权限时，MajMusic 的默认数据根为 `/storage/emulated/0/Documents/MajMusic`，并在首次迁移时保留应用私有目录中的旧配置、索引和缓存。没有共享存储权限时使用应用私有目录作为可用回退，登录配置仍能正常工作。默认根下会创建 `Cache`、`Cache/Temp`、`Runtime`、`MaiCharts`、`Skins`、`Logs`、`RecordOutputs`、`AudioMetadata` 和 `DocumentCovers`。

# Android 原生凭据验证

```powershell
./Tools/Test-AndroidCredentials.ps1 -Serial <adb-device-serial>
```

脚本自动查找 .NET SDK、Android SDK 与兼容的 Android Studio、Unity 或独立 JDK，构建并安装独立测试包 `com.icecloud.majmusic.tests`。测试使用随机值检查写入、读取、跨实例保留、更新和删除，共 12 项原生 Keystore 断言；完成后卸载测试包。日志保存在 `artifacts/android-credentials-*`。

`-CredentialStoreSource <旧版源码路径>` 可链接旧实现做失败对照，`-KeepInstalled` 保留测试包用于检查。工具位置可通过 `-DotNet`、`-AndroidSdkDirectory` 和 `-JavaSdkDirectory` 指定。

# Windows 切页测量

[NativeNavigation](Diagnostics/NativeNavigation/README.md) 使用生成曲库和独立 Windows 窗口，记录切页调用、首次布局、界面帧回调及调度间隔，自动输出报告并关闭窗口。每次构建使用独立输出目录，可对照页面预热、卡片复用与切换前后的耗时。
