# 开发验证

## 构建与回归

```sh
dotnet build MajMusic.sln
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj
```

默认回归覆盖数据存储、目录类型、收藏夹格式、账号选择与自动登录、网络与缓存、播放队列、音频设置，以及界面交互。

内存回归可单独运行：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- catalog-memory presentation-retention
```

覆盖大曲库首屏预览的持有范围、重复刷新后旧曲库的回收、4K 与窄长封面限尺寸解码，以及快速切换、清除封面后的资源释放。

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
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- browse-placeholder-lifecycle source-transport
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- cover-preload browse-entry catalog-read-ordering
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- recommendation-paging recommendation-paging-layout recommendation-playback card-feedback
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- playback-region-motion playlist-mirrors context-actions context-menus-layout
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- mobile-safe-area mobile-player-gesture surface-geometry default-favorites download-metadata
```

测试使用与桌面入口相同的 Arc 注册和主题。手机界面覆盖 360×640、390×844、430×932，检查安全区、软键盘收缩、底部导航、触摸目标、详情抽屉，以及桌面与窄屏切换时账号和 Guest 选择不变。该检查在 Skia 离屏窗口中运行；Android 安装包检查见下文，iOS 宿主待接入。

布局检查关闭动画以固定截图。`arc-motion` 使用应用默认的完整动效，逐帧检查按钮按压与恢复、模式滑块、页面横向位移，以及搜索框从右侧图标展开、收起时的宽度、锚点与菜单对齐；同时验证快速反转和窗口缩放后的布局。中间帧保存在 `.work/ui-arc-motion-*.png`。

`surface-motion` 用 Arc 可控时钟检查横向推移、缓入缓出速度、像素尺寸、快切、中断与快照释放；`browse-loading` 检查真实点击后立即出现有限骨架、慢请求隔离及首屏卡片懒创建，加载完成直接替换占位；`appearance` 检查三种外观的即时应用、保存、重启恢复及深浅渲染。

`browse-placeholder-lifecycle` 逐帧检查慢加载、失败和空推荐时保留完整占位、刷新时保留已有内容，以及横移时新面板不透出旧封面、切换结束后不残留旧内容。`source-transport` 使用本机监听验证实际请求 UA、登录 Cookie、重定向隔离与取消；Windows 同时检查系统 WinHTTP 的连接策略。

`cover-preload` 检查有限封面预热、共享请求、超时、取消和独立图片引用；`browse-entry` 在真实卡片和横移中延迟查询及封面，检查动画中占位保持、首次入场已有封面、返回时复用、过期结果隔离，以及顶部导航的滑块和页面使用缓入缓出节奏。

`catalog-read-ordering` 检查同页移除歌单曲目期间的并发查询与封面准备，旧读取不得覆盖新成员、元数据或已预热的首屏。

推荐分页检查覆盖每页 50 首、总量上限 150、账号独立页号、分批加载与取消后续取；桌面和窄屏使用真实滚动、底部页码点击及横移中间帧验证。播放检查覆盖先播放首批、后续批次补齐队列、随机顺序与导航回滚，补齐期间不重开音频。卡片反馈检查覆盖悬停与选中的中间帧、下载完成提示、控件回收、减少及关闭动画模式，下载填充保持原色。

播放器区域检查覆盖深浅外观和实际显示缩放下的封面、文字与播放图标中间帧，原始图片释放、快速切歌及控件回收。同步收藏检查覆盖精确账号绑定、单一 MajNet 入口、完整成员和本地编辑在旧云端快照及重启后的保留。菜单检查用真实右键、键盘和触摸操作验证目标归属、歌单连续滚动、重命名、导出与删除。

`home-ui` 包含两万四千首曲目的整批通知、卡片懒创建、加载状态所有权与模式快切回归；`storage` 使用真实 SQLite 数据库锁验证界面队列可继续运行，以及取消后的结果隔离。

`mobile-safe-area` 使用生产安全区适配器与真实 Arc Page，检查 140 物理像素在 3.25 倍缩放下只应用一次、屏幕缺口与底栏变更、缩放通知和移除后的解绑。`mobile-player-gesture` 使用真实触摸验证上滑打开实际播放歌曲、浏览另一首时的归属，以及列表滚动、按钮和进度拖动互不干扰。`surface-geometry` 在多种显示缩放下检查手机选中项间距、均分触摸区域和桌面详情封面占宽；已有 `surface-motion` 同时检查横移首帧的封面像素尺寸保持不变。

`default-favorites` 检查新工作区不创建默认歌单，仅清理旧版生成且没有成员或云端绑定的空列表，保留手建、导入和同步的歌单。`download-metadata` 检查完成下载后直接解析本地文件、原卡片与详情即时更新、旧查询隔离、原生属性优先、关闭取消和有界保留。

下载与播放准备的专项回归：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- media-cache-concurrency transport-responsiveness audio-download-progress card-download-ui card-download-layout
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- offline-covers auth-boundaries
```

覆盖慢速传输期间切歌与取消、真实字节进度、缓存命中，以及卡片由下向上的填充、完成状态和独立选中描边。

`offline-covers` 检查音频和封面一并保留、清缓存后重启离线使用、接口拒绝访问时优先读取已有文件、旧播放缓存转为手动下载、多接入点缓存隔离，以及封面补取的取消与重试。`portable-data` 同时检查旧缓存的迁移与归属；`auth-boundaries` 区分接入点浏览器验证、普通访问拒绝和登录过期，并检查自动登录提示及可选账号资料读取失败时保留已经成功取得的云端歌单。

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

## 内存与发布测量

2026-11-16，Windows x64 Release，同一份 21,554 首曲库的离线副本。测量仅创建共享 ViewModel，各阶段等待操作完成并收集后读取托管存活量与进程私有内存，未创建窗口或音频引擎。

| 场景 | 指标 | 修改前 | 修改后 |
| --- | --- | ---: | ---: |
| 初始化推荐页 | 托管存活量 | 44.43 MiB | 2.41 MiB |
| 打开曲库 | 托管存活量 | 88.38 MiB | 46.36 MiB |
| 连续刷新曲库 4 次 | 进程私有内存 | 246.52 MiB | 156.63 MiB |

原开发 Release 目录约 918.52 MiB；单平台发布实测如下，均包含全部 321 个 x64 VLC 插件：

| 发布方式 | 程序目录 | ZIP |
| --- | ---: | ---: |
| 自带 .NET 运行时 | 231.54 MiB | 101.89 MiB |
| 使用已安装的 .NET 10 x64 运行时 | 155.07 MiB | 66.81 MiB |

两包均检查了架构、依赖清单、符号和本地数据，并分别从发布目录加载原生 VLC，完成生成 WAV/MP3 的解码、暂停、进度拖动与切歌检查，使用静音测试输出。

## Android ARM64

2026-11-17，使用 .NET 10、Android SDK 36 和 JDK 17.0.18 构建。Debug 编译 0 警告、0 错误；Release 通过 [发布脚本](Tools/README.md) 生成自带运行时的开发签名 APK，约 38.81 MiB。

已完成的包检查：

- 只包含 `arm64-v8a`，最低 API 23，目标 API 36，Release 不开启 `debuggable`。
- 14 个原生库均为 AArch64；44 个 ELF LOAD 段与 APK 中的原生库位置通过 16 KB 对齐检查。
- APK v1、v2、v3 签名验证通过；包含第三方声明，无桌面 VLC、运行数据或签名私钥。
- 合并 Manifest 包含 Application、Launcher 和 `mediaPlayback` 前台服务，以及播放、网络和通知权限。

此前版本的共享逻辑 66 组默认回归通过。`document-storage` 通过真实工作区和模拟文档提供者检查嵌套曲库、TXT 标题与 Maj 哈希、保留原音频 URI、不复制音乐、封面缓存、只读授权入口、Maj JSON 导入导出和桌面选择器回退。

```powershell
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- document-storage
./Tools/Publish-Android.ps1
```

2026-11-17，在 Android 16（API 36）ARM64 手机通过 USB 实测：修正入口主题继承 AppCompat 后，覆盖安装成功，连续三次冷启动均保持进程存活并进入本应用前台，对应进程无致命异常。主页、设置和目录可切换，退到系统桌面后可返回原 Activity。复测命令：

```powershell
./Tools/Test-AndroidStartup.ps1 -Serial <adb-device-serial>
```

同日完成 Android Keystore 独立真机回归：独立测试应用链接生产凭据存储，12 项断言覆盖写入、跨实例读取、第二账号隔离、更新和删除。旧实现首写后读取复现 `AEADBadTagException`；复用原生 `IKey` 后两轮均通过，测试应用随后卸载。该测试的源码对照、命令与日志位置见 [Android 原生凭据测试](MajMusic.Android.Tests/README.md)。

手机实测确认 Guest 曲库可读取；用户名与电脑一致后，账号登录成功。更新后冷启动自动登录返回 200 并取得会话，空密码框弹出 QWERTY 密码键盘，没有中文候选。`auth-boundaries` 和 `endpoint-cards` 覆盖登录 401 的“账号或密码错误”、普通会话 401 的“请重新登录”，以及权限拒绝与浏览器验证的区分。`mobile-account-input` 检查真实手机模板的密码键盘选项、禁用联想与自动大写、用户名大小写和密码的双向绑定。

同日手机检查确认：全屏视口为 1200×2670、缩放 3.25，140 像素的顶部安全区仅保留一次；导航栏起点为 43.0769 逻辑像素。静音播放期间，从底部播放器上滑打开当前歌曲详情；下载另一首歌后，同页卡片和详情从未知时长直接更新为 02:14、128 kbps、44.1 kHz。

后续增加滑条即时跟随、点击底部信息打开详情，以及宽松、紧凑、小型三种列表格式。本轮按要求检查 Release 编译、覆盖安装与启动显示。

封面快捷播放使用桌面与手机共用控件，点击和右拖共用一条可逆轨迹；歌曲详情沿用 Arc 模板，以单一进度控制展开、遮罩与收回，取消原弹簧反向时的速度延续。小型卡片将下载状态放到独立内容行。此次检查范围为桌面 Release 编译、Android 覆盖安装和启动显示。

播放图标的位置和大小沿单条连续曲线同步变化。显示帧按剩余的实际位移做指数减速，先快后慢；曲线区间导数将中心与放大边缘速度限制在每秒 2200 逻辑像素以内，尾段在 0.12 逻辑像素内结束。点击、手拖和回退共用此响应。底部播放器上滑采用独立的直接位置映射，小幅上滑松手即可展开；点击或松手后的详情过渡为 230ms 的缓出曲线。弹层底边取实际导航栏上沿，底栏换页时收起详情。

进度拖动松手后保留本次目标，跳转完成时直接接入后端确认的位置。新拖动继承当前显示进度；取消、切歌和失败解除对应目标。桌面端取消被新拖动替代的旧跳转时，保留音频播放状态。

此轮桌面 Release 编译为 0 警告、0 错误；Android ARM64 包为 38.83 MiB，通过发布脚本的包检查。手机覆盖安装成功，冷启动 `Status: ok`，截图确认主页与封面卡片显示。按本轮要求未进行播放、拖动或动画的功能复测。

锁屏媒体按钮、耳机与音频焦点、实际目录提供者待在对应设备验证。

## 共享媒体与手动清理

2026-11-17，媒体储存支持独立目录与 MajdataPlay 共享目录，分别保留路径；桌面配置、数据库与媒体地址索引仍在 exe 目录。媒体使用 `MaiCharts`、`Cache/Objects/xx/SHA256` 和 `Runtime` 层级。本地歌单以标准 Maj JSON 镜像到 `MaiCharts/MajMusic-*.json`，自有标记保留原列表 ID，更新与删除只修改自己的镜像。

共享读取核对了当前 Play 的公共 `OnlineServers`、`OnlineChartCache` 和旧 `Cache/Net` 路径。文件复用校验 SHA-256；已有旧缓存核对 sidecar。新版资源按真实 `/info` 信息登记公共引用，保留难度、七级谱面哈希和其他资源描述；不同版本不直接覆盖。删除前合并 Music 与 Play 的其他引用保护集，批量清理复用一次库快照；共享文件发布与删除使用同一写入门。

音乐统一为云端／本地，完整播放准备和下载都保留已取得的文件，不启动自动清理。右键与长按菜单提供“删除本地文件”；设置可手动选择 7 天前、30 天前或全部本地音乐。导入的原曲库文件不参与删除。删除后清理对应卡片、封面、元数据任务和播放资源身份，保留队列及歌单关系。

Android 共享树使用独立的读写授权；普通曲库仍只读。对象先写临时文档再重命名提交，自有歌单更换失败时保留或恢复旧文件。Play 当前 Android Net 对象和数据库位于其私有目录，公开 Documents 内容可共享，私有数据不参与。Play 进程内对象索引在重启后读取新增对象。

本轮检查范围为桌面 Release 编译、Android ARM64 发布检查、覆盖安装与启动界面。按要求未运行详细功能、批量删除、双进程读写或其他桌面平台的运行测试。

结果：桌面 Release 为 0 警告、0 错误；最终 Android 包 38.89 MiB，无 C# 编译警告，通过发布包检查。覆盖安装 `Success`，冷启动 `Status: ok`、约 1.93 秒；截图确认主页、封面与“已在本地”状态显示。

## 共享目录刷新与 PC 实测

2026-11-17，Windows PC 的 `shared-play-storage` 与显式 `shared-play-storage-live` 均通过。合成回归先启动 Music 并保留云端卡片，再由外部写入不同接入点目录名下的旧版 `maidata.txt` 与音频，不提供 SHA sidecar；再次搜索即附加本地文件，原卡片更新为“已在本地”，在线曲目身份保持不变。旧文件先按 maidata MD5、谱面版本与目录哈希确认归属，再检查完整文件并计算媒体 SHA-256；已有资源摘要时严格比对。错误 maidata、未完成分片和匿名来源音频冲突均被拒绝，已知正确摘要可消歧。搜索刷新只读公共元数据，文件校验限于初始批次与可见卡片。

真实数据验证只读提取公开接入点信息与目标歌曲，清空账号后写入隔离工作区，禁止 HTTP 与凭据访问；音频、maidata、原设置、Music 数据库和 Play 公共数据库的前后指纹一致。桌面实际界面搜索“讲数学”后，`“人民叫师”讲数学` 显示“已在本地”及 `05:24`。

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- directory-sources document-storage directory-ui shared-play-storage
```

`shared-play-storage-live` 不在默认回归中，需明确提供 `MAJMUSIC_SHARED_PLAY_ROOT`、`MAJMUSIC_SHARED_PLAY_SETTINGS`、`MAJMUSIC_SHARED_PLAY_LIBRARY`、`MAJMUSIC_SHARED_PLAY_AUDIO`、`MAJMUSIC_SHARED_PLAY_CHART_HASH` 与 `MAJMUSIC_SHARED_PLAY_AUDIO_SHA256`；摘要写入 `artifacts/shared-play-live/`，不记录账号或凭据。

最终运行上述四组与显式 live 组，五组全部通过；文档目录卡片断言同步为“在云端／已在本地”的状态语义。桌面 Debug、Release 均为 0 警告、0 错误。

本轮 Android 存储改为普通路径与“文件权限”主入口，授权返回后在原操作中保存并扫描；SAF 选择器保留为备选，选目录后也立即应用。设置区分生效的媒体目录与私有配置目录，“读取全部”包含共享曲库。ARM64 最终包位于 `artifacts/android-arm64-20261117-062123-892e2957/`，38.91 MiB，无 C# 编译警告，通过签名、单 ARM64 ABI 和 16 KB 原生库对齐检查。按用户要求，本轮未安装或进行设备验证。
