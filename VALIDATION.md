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
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- surface-motion browse-loading appearance
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- cover-preload browse-entry catalog-read-ordering
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- recommendation-paging recommendation-paging-layout recommendation-playback card-feedback
```

测试使用与桌面入口相同的 Arc 注册和主题。手机界面覆盖 360×640、390×844、430×932，检查安全区、软键盘收缩、底部导航、触摸目标、详情抽屉，以及桌面与窄屏切换时账号和 Guest 选择不变。该检查在 Skia 离屏窗口中运行；Android/iOS 宿主与实机播放另行验证。

布局检查关闭动画以固定截图。`arc-motion` 使用应用默认的完整动效，逐帧检查按钮按压与恢复、模式滑块、页面横向位移，以及搜索框从右侧图标展开、收起时的宽度、锚点与菜单对齐；同时验证快速反转和窗口缩放后的布局。中间帧保存在 `.work/ui-arc-motion-*.png`。

`surface-motion` 用 Arc 可控时钟检查横向推移、缓入缓出速度、像素尺寸、快切、中断与快照释放；`browse-loading` 检查真实点击后立即出现有限骨架、慢请求隔离及首屏卡片懒创建，加载完成直接替换占位；`appearance` 检查三种外观的即时应用、保存、重启恢复及深浅渲染。

`cover-preload` 检查有限封面预热、共享请求、超时、取消和独立图片引用；`browse-entry` 在真实卡片和横移中延迟查询及封面，检查动画中占位保持、首次入场已有封面、返回时复用、过期结果隔离，以及顶部导航的滑块和页面使用缓入缓出节奏。

`catalog-read-ordering` 检查同页移除歌单曲目期间的并发查询与封面准备，旧读取不得覆盖新成员、元数据或已预热的首屏。

推荐分页检查覆盖每页 50 首、总量上限 150、账号独立页号、分批加载与取消后续取；桌面和窄屏使用真实滚动、底部页码点击及横移中间帧验证。播放检查覆盖先播放首批、后续批次补齐队列、随机顺序与导航回滚，补齐期间不重开音频。卡片反馈检查覆盖悬停与选中的中间帧、下载完成提示、控件回收、减少及关闭动画模式，下载填充保持原色。

`home-ui` 包含两万四千首曲目的整批通知、卡片懒创建、加载状态所有权与模式快切回归；`storage` 使用真实 SQLite 数据库锁验证界面队列可继续运行，以及取消后的结果隔离。

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
