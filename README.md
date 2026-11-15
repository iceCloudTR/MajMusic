# MajMusic
基于 Avalonia、TKCode.Arc 和 .NET 10 的音乐播放器，支持本地曲库与兼容 MajNet 的音乐来源。

# 还在头痛不能随时随地听MajNet上的音乐吗？！
# 立刻启动 MajMusic！！！

## 功能

- 首页默认浏览“猜你喜欢”，可切换歌单、曲库；点击搜索图标展开搜索。
- 推荐、歌单、曲库和搜索结果统一使用封面卡片，随窗口宽度调整列数，长列表向下滚动。
- 本地音乐播放、暂停、拖动进度、切歌、随机播放与循环模式。
- 歌曲列表显示时长、码率、采样率和来源，右侧展示封面与歌曲操作。
- 多个本地收藏夹，支持纯本地、纯在线和混合歌单，可删除歌单或移除单曲。
- Maj 收藏夹 JSON 导入、导出，云端收藏夹读取与按账号自动上传。
- 多接入点、独立账号与 Guest，支持记住密码和自动登录。
- 应用音量、静音、输出设备选择与离线下载。
- 歌曲卡片右半区显示下载进度，从下向上填充，完成后保留颜色并显示“下载完成”。
- Arc 控件与统一主题，桌面保留常驻详情栏，手机窄屏使用底部导航、迷你播放器与歌曲详情抽屉。
- 按钮按压、分段滑块、下拉菜单和抽屉使用 Arc 动效；搜索框从图标平滑展开、收起，页面切换短暂淡入。

## 开发运行

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，使用 Rider 或其他支持 .NET 的 IDE 打开 `MajMusic.sln`。

```sh
dotnet restore MajMusic.sln
dotnet run --project MajMusic/MajMusic.csproj
```

Windows 构建自带 LibVLC；Linux 和 macOS 需要安装对应的 LibVLC 运行库。当前桌面开发验证环境为 Windows，移动端宿主仍在开发计划中。

界面采用 TKCode.Arc 1.1.0 和 Avalonia 12.1.5，共享现有 ViewModel、命令与服务。窄屏界面已使用 Arc.Mobile 组件适配安全区、软键盘和触摸区域；Android/iOS 应用宿主、后台音频服务和系统媒体控制仍需接入。[上游移动支持状态](https://github.com/AIDotNet/TKCode.Arc#status-and-compatibility)目前也区分了桌面手机框预览与实机验证。

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

在“设置”页选择音频输出设备、调整音量或清理缓存。音频设置实时生效并保存。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `MajMusic` | 桌面入口与窗口生命周期 |
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
