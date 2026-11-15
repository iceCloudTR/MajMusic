# 开发验证

## 构建与回归

```sh
dotnet build MajMusic.sln
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj
```

默认回归覆盖数据存储、目录类型、收藏夹格式、账号选择与自动登录、网络与缓存、播放队列、音频设置，以及界面交互。

云端收藏夹的接口与同步检查使用隔离的模拟服务，覆盖私有歌单创建、账号收藏订阅、增删曲目、离线补传和账号隔离。

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- collection-network cloud-collection-preferences cloud-collection-sync
```

可在命令末尾指定测试组：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- playback account-ui directory-ui audio-settings-ui headless
```

界面测试使用真实 Avalonia 控件验证均分按钮、居中播放控制、封面卡片、虚拟化滚动和键盘选择，并生成深色、浅色、紧凑窗口和缩放渲染，结果位于 `.work/`。

Arc 界面检查：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- headless alignment endpoint-cards-layout card-download-layout arc-responsive
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- arc-motion
```

测试使用与桌面入口相同的 Arc 注册和主题。手机界面覆盖 360×640、390×844、430×932，检查安全区、软键盘收缩、底部导航、触摸目标、详情抽屉，以及桌面与窄屏切换时账号和 Guest 选择不变。该检查在 Skia 离屏窗口中运行；Android/iOS 宿主与实机播放另行验证。

布局检查关闭动画以固定截图。`arc-motion` 使用应用默认的完整动效，逐帧检查按钮按压与恢复、模式滑块、换页淡入，以及搜索框从右侧图标展开、收起时的宽度、锚点与菜单对齐；同时验证快速反转和窗口缩放后的布局。中间帧保存在 `.work/ui-arc-motion-*.png`。

`home-ui` 包含两万四千首曲目的整批通知与模式快切回归；`storage` 使用真实 SQLite 数据库锁验证界面队列可继续运行，以及取消后的结果隔离。

下载与播放准备的专项回归：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- media-cache-concurrency transport-responsiveness audio-download-progress card-download-ui card-download-layout
```

覆盖慢速传输期间切歌与取消、真实字节进度、缓存命中，以及卡片由下向上的填充、完成状态和独立选中描边。

## 原生检查

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- desktop-audio desktop-integration desktop-metadata desktop-audio-controls
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- native-vault
```

| 测试组 | 内容 |
| --- | --- |
| `desktop-audio` | WAV、MP3 解码与播放控制 |
| `desktop-integration` | 本地索引、共享界面和原生播放的完整流程 |
| `desktop-metadata` | 独立读取音频参数 |
| `desktop-audio-controls` | 音量、静音、设备枚举和输出回退 |
| `native-vault` | 生成凭据的保存、重开、隔离和删除 |
| `desktop-output` | 通过默认设备播放生成的测试音频 |

音频控制检查使用内存输出；`desktop-output` 单独运行时会播放测试音。

当前原生验证环境为 Windows x64。macOS、Linux 和实际设备切换需在对应环境验证。
