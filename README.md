# MajMusic
基于 Avalonia、TKCode.Arc 和 .NET 10 的音乐播放器，支持本地曲库与兼容 MajNet 的音乐来源。

# 还在头痛不能随时随地听MajNet上的音乐吗？！
# 立刻启动 MajMusic！！！

## 功能

- 首页默认浏览“猜你喜欢”，可切换歌单、曲库；点击搜索图标切到曲库并展开搜索。
- 推荐最多 150 首，每页 50 首，滚到列表底部选择页码，翻页沿用左右横移；播放队列连续使用整批推荐。
- 推荐、歌单、曲库和搜索结果统一使用封面卡片，随窗口宽度调整列数，长列表向下滚动。
- 本地音乐播放、暂停、拖动进度、切歌、随机播放与循环模式。
- 歌曲列表显示时长、码率、采样率和来源，右侧展示封面与歌曲操作。
- 多个本地收藏夹，支持纯本地、纯在线和混合歌单，可删除歌单或移除单曲。
- 歌单管理使用连续纵向滚动，歌曲与歌单提供播放、收藏、加入、移除、重命名和导出等右键操作。
- Maj 收藏夹 JSON 导入、导出，云端收藏夹读取与按账号自动上传。
- 多接入点、独立账号与 Guest，支持记住密码和自动登录。
- 应用音量、静音、输出设备选择与离线下载，下载音乐时同时保留封面。
- 歌曲卡片右半区显示下载进度，从下向上填充，完成后保留颜色并显示“下载完成”。
- Arc 控件与统一主题，桌面保留常驻详情栏，手机窄屏使用底部导航、迷你播放器与歌曲详情抽屉。
- 按钮、分段滑块、下拉菜单和抽屉使用 Arc 动效；页面与歌曲面板按导航方向左右横移，使用缓入缓出曲线，搜索框从图标平滑伸缩。
- 卡片悬停与选中使用柔和描边反馈，下载完成时勾选图标轻弹确认；减少动画时保留淡入反馈，关闭动画时直接显示结果。
- 切歌时封面和底部歌曲信息平滑衔接，播放、暂停图标在原位置过渡。
- 已同步到 MajNet 的“我喜欢”共用同一入口，保留已有本地与在线歌曲。
- 提前准备推荐、歌单和曲库的首屏歌曲与封面；加载较慢时保持卡片占位，横移结束后再更新，只创建实际浏览到的歌曲卡片。
- 推荐加载失败或暂无内容时保留卡片占位，重新加载已有内容时保留原歌曲。
- 深色、浅色与跟随系统外观，在设置中即时切换并保存。

## 开发运行

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，使用 Rider 或其他支持 .NET 的 IDE 打开 `MajMusic.sln`。

```sh
dotnet restore MajMusic.sln
dotnet run --project MajMusic/MajMusic.csproj
```

Windows 构建自带 LibVLC；Linux 和 macOS 需要安装对应的 LibVLC 运行库。Android 使用系统音频接口，生成 ARM64 安装包。

Windows 网络请求使用系统 WinHTTP，其他平台使用对应的 .NET 网络接口；兼容请求使用 `MajdataPlay/2.1.0` UA，各接入点独立管理登录会话。

Windows 分发使用 [发布脚本](Tools/README.md)：默认生成自带 .NET 运行时的 x64 ZIP，也可生成依赖已安装运行时的较小版本。发布包只包含对应架构，保留完整音频插件。

界面采用 TKCode.Arc 1.1.0 和 Avalonia 12.1.5，共享 ViewModel、命令与服务。窄屏使用 Arc.Mobile 组件适配安全区、软键盘和触摸区域。Android 宿主接入后台播放、通知栏与锁屏媒体控制；iOS 宿主待接入。

Android 开发打开 `MajMusic.Android.sln`，安装 .NET 10 Android workload、Android SDK 36 与 JDK 17 或 21 后运行：

```powershell
./Tools/Publish-Android.ps1
```

安装包输出到 `artifacts/android-arm64-*/MajMusic-android-arm64.apk`。首次测试包使用开发签名，构建详情见 [Tools/README.md](Tools/README.md)。

Arc 的 MIT 许可证和随包第三方声明保留在 [ThirdPartyNotices](ThirdPartyNotices/)，构建和发布时会一同复制。

## 使用

在“目录”页分别配置来源：

| 类型 | 内容 |
| --- | --- |
| 曲库 | 含音乐、`maidata.txt` 和封面的歌曲目录，可包含多层子目录。 |
| 收藏夹索引 | `majcollection` 或存放 Maj 收藏夹 JSON 的目录。 |

曲库直接引用原文件。歌曲名读取 `maidata.txt` 的 `&title`；常见文件组合为 `track.mp3`、`maidata.txt`、`bg.png`。

在“账号”页添加接入点，每张卡片独立配置地址和账号或 Guest，多个接入点可同时登录使用。勾选“记住密码”和“自动登录”后，在线模式会登录所选账号。首次运行离线，在线开关自动保存并在下次启动时恢复；密码保存在系统凭据库中。

选定已保存的账号，勾选“使用此接入点的收藏夹”。Music 会把本地歌单中属于该服务的在线曲目同步为私有云端歌单，并加入该账号的收藏。喜欢、添加或移除歌曲、删除歌单后自动更新；离线修改在下次联网并登录该账号后上传。每个账号独立保存此选择，本地音乐保留在本地歌单中。

在 Play 登录同一接入点和账号，再刷新在线收藏夹，即可读取同步的歌单。[MajNet 的歌单说明](https://github.com/TeamMajdata/MajdataDocs/blob/main/src/majdatanet/feature/collection.md)介绍了私有歌单与收藏的对应关系。

PC 版配置与数据保存在程序所在目录：`settings.json`、`library.db`、`cloud-collections.db`，以及 `Cache`、`Downloads`、`AudioMetadata` 子目录。旧版应用数据会在首次启动时迁移到此处。

Android 在“目录”页选择系统授权的曲库或收藏夹目录，递归读取原文件，保留目录权限；音乐不复制，封面按需缓存。配置、索引和下载保存在应用私有目录，记住的密码由 Android Keystore 加密。音频输出跟随系统当前设备。

在“设置”页选择音频输出设备、调整音量或清理缓存。音频设置实时生效并保存。

音乐与封面优先读取本地文件；接入点暂时不可用时，已缓存的内容仍可使用。手动下载会保留音频与封面，清理缓存不会删除这些下载。

“设置 → 外观”可选择深色、浅色或跟随系统。桌面与窄屏共享同一选择，重新启动后恢复。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `MajMusic` | 桌面入口与窗口生命周期 |
| `MajMusic.Android` | ARM64 安卓入口、系统音频与后台媒体服务 |
| `MajMusic.Core` | 数据模型、接口与播放队列 |
| `MajMusic.Infrastructure` | 本地索引、SQLite、网络、缓存与凭据 |
| `MajMusic.Audio.Desktop` | LibVLC 音频播放与输出控制 |
| `MajMusic.Presentation` | 共享界面与交互 |
| `MajMusic.Tests` | 回归测试与界面渲染 |

## 测试

```sh
dotnet build MajMusic.sln
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj
```

原生音频、设备与凭据测试命令见 [VALIDATION.md](VALIDATION.md)。
