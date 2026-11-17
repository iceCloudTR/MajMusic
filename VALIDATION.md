# 开发验证

当前版本使用 Music 独立媒体存储。文末保留的 Play 共享、数据库只读和自动镜像章节均为已移除功能的历史；本轮检查范围见“独立媒体存储”。

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

## 历史：共享媒体与手动清理（功能已移除）

2026-11-17，媒体储存支持独立目录与 MajdataPlay 共享目录，分别保留路径；桌面配置、数据库与媒体地址索引仍在 exe 目录。媒体使用 `MaiCharts`、`Cache/Objects/xx/SHA256` 和 `Runtime` 层级。本地歌单以标准 Maj JSON 镜像到 `MaiCharts/MajMusic-*.json`，自有标记保留原列表 ID，更新与删除只修改自己的镜像。

共享读取核对了当前 Play 的公共 `OnlineServers`、`OnlineChartCache` 和旧 `Cache/Net` 路径。文件复用校验 SHA-256；已有旧缓存核对 sidecar。新版资源按真实 `/info` 信息登记公共引用，保留难度、七级谱面哈希和其他资源描述；不同版本不直接覆盖。删除前合并 Music 与 Play 的其他引用保护集，批量清理复用一次库快照；共享文件发布与删除使用同一写入门。

音乐统一为云端／本地，完整播放准备和下载都保留已取得的文件，不启动自动清理。右键与长按菜单提供“删除本地文件”；设置可手动选择 7 天前、30 天前或全部本地音乐。导入的原曲库文件不参与删除。删除后清理对应卡片、封面、元数据任务和播放资源身份，保留队列及歌单关系。

Android 共享树使用独立的读写授权；普通曲库仍只读。对象先写临时文档再重命名提交，自有歌单更换失败时保留或恢复旧文件。Play 当前 Android Net 对象和数据库位于其私有目录，公开 Documents 内容可共享，私有数据不参与。Play 进程内对象索引在重启后读取新增对象。

本轮检查范围为桌面 Release 编译、Android ARM64 发布检查、覆盖安装与启动界面。按要求未运行详细功能、批量删除、双进程读写或其他桌面平台的运行测试。

结果：桌面 Release 为 0 警告、0 错误；最终 Android 包 38.89 MiB，无 C# 编译警告，通过发布包检查。覆盖安装 `Success`，冷启动 `Status: ok`、约 1.93 秒；截图确认主页、封面与“已在本地”状态显示。

## 历史：共享目录刷新与 PC 实测（功能已移除）

2026-11-17，Windows PC 的 `shared-play-storage` 与显式 `shared-play-storage-live` 均通过。合成回归先启动 Music 并保留云端卡片，再由外部写入不同接入点目录名下的旧版 `maidata.txt` 与音频，不提供 SHA sidecar；再次搜索即附加本地文件，原卡片更新为“已在本地”，在线曲目身份保持不变。旧文件先按 maidata MD5、谱面版本与目录哈希确认归属，再检查完整文件并计算媒体 SHA-256；已有资源摘要时严格比对。错误 maidata、未完成分片和匿名来源音频冲突均被拒绝，已知正确摘要可消歧。搜索刷新只读公共元数据，文件校验限于初始批次与可见卡片。

真实数据验证只读提取公开接入点信息与目标歌曲，清空账号后写入隔离工作区，禁止 HTTP 与凭据访问；音频、maidata、原设置、Music 数据库和 Play 公共数据库的前后指纹一致。桌面实际界面搜索“讲数学”后，`“人民叫师”讲数学` 显示“已在本地”及 `05:24`。

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- directory-sources document-storage directory-ui shared-play-storage
```

`shared-play-storage-live` 不在默认回归中，需明确提供 `MAJMUSIC_SHARED_PLAY_ROOT`、`MAJMUSIC_SHARED_PLAY_SETTINGS`、`MAJMUSIC_SHARED_PLAY_LIBRARY`、`MAJMUSIC_SHARED_PLAY_AUDIO`、`MAJMUSIC_SHARED_PLAY_CHART_HASH` 与 `MAJMUSIC_SHARED_PLAY_AUDIO_SHA256`；摘要写入 `artifacts/shared-play-live/`，不记录账号或凭据。

最终运行上述四组与显式 live 组，五组全部通过；文档目录卡片断言同步为“在云端／已在本地”的状态语义。桌面 Debug、Release 均为 0 警告、0 错误。

本轮 Android 存储改为普通路径与“文件权限”主入口，授权返回后在原操作中保存并扫描；SAF 选择器保留为备选，选目录后也立即应用。设置区分生效的媒体目录与私有配置目录，“读取全部”包含共享曲库。ARM64 最终包位于 `artifacts/android-arm64-20261117-062123-892e2957/`，38.91 MiB，无 C# 编译警告，通过签名、单 ARM64 ABI 和 16 KB 原生库对齐检查。按用户要求，本轮未安装或进行设备验证。

## 历史：Play 数据库只读边界（随后已移除读取）

2026-11-17，移除共享资源登记入口及音频解析中的两次调用。Music 不再创建、迁移或更新 Play 的 `Runtime/runtime.db`；读取曲目与清理引用保护仍使用 `SqliteOpenMode.ReadOnly`。Music 自己的索引与音乐、封面、谱面文件预缓存继续保留。上文的资源登记实现为历史记录，已由本节替代；本次没有回写或清理玩家既有 Play 数据库。

`shared-play-storage` 新增真实工作区文件发布与再次复用回归：已有 Play 数据库的文件指纹、结构和哨兵数据全部不变；无数据库时不创建数据库及 SQLite 附属文件。两种情况下均校验共享音乐、封面、谱面对象以及 Music 私有索引，禁止 HTTP 和凭据访问。该组通过，`media-cache-concurrency` 通过。

刷新语义为按需读取：重新搜索、卡片重新加载及“读取全部”可发现 Play 新缓存，无需重启；没有后台目录监听。当前 Play 源码的启动顺序是对象扫描与清理先于 Net 同步；未被自身数据库引用的对象会被清理，运行中的对象索引也不会自动发现外部新增文件。已知摘要且已索引的对象可直接命中。这里只核对 Play 源码，未修改、构建或运行 Play。

本次桌面 Release 编译通过（0 警告、0 错误）；Android ARM64 发布通过签名、ABI 与 16 KB 对齐检查，包位于 `artifacts/android-arm64-20261117-163347-346d3772/`，38.90 MiB，未安装或实机验证。正在使用的桌面 Debug 进程保持运行，未覆盖其二进制；Rider 下一次运行会编译新的源码。

扩展运行 `audio-download-progress` 时，已有的 `ValidatePlaybackProgressAsync` 在第 240 行等待首段下载超时。在隔离的 `164c378` 基线副本单独运行该组，复现同一位置的超时；该失败早于本次数据库写入移除。本轮没有修改播放进度实现，也没有将该组报告为通过。

## 独立媒体存储

2026-11-17，删除 Play 共享模式、数据库与旧 Net 缓存读取、资源登记、谱面预缓存和自动 `MaiCharts` 歌单镜像。媒体只由 Music 自己的索引管理；旧共享配置退回默认媒体目录，旧在线曲目记录中的其他应用路径不再视为 Music 已下载文件。原文件保留。显式本地曲库导入、标准 Maj 歌单 JSON 导入导出、在线来源和云端收藏功能保持。

上面共享目录、只读数据库和自动镜像的章节保留为历史记录，其命令与结果不代表当前功能；`shared-play-storage`、`shared-play-storage-live` 测试组及真实用户数据入口已删除。

新增合成测试组：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- independent-media-storage
```

覆盖旧配置迁移、外部数据库锁定时的本地搜索、旧 Play 文件路径的卡片状态与离线播放拒绝、清理保留外部原文件、Music 下载与重启离线复用、单曲删除和 30 天／7 天／全部手动清理。另检查原生元数据读取不会绕过远程路径的归属检查、显式导入原曲库仍可解析音频参数、标准 Maj JSON 的显式导出与重新读取，以及普通保存歌单不生成外部镜像。所有数据均在临时目录创建，HTTP 使用模拟处理器，不读取用户账户、设备或原曲库。

最终运行 `independent-media-storage`、`storage`、`directory-sources`、`document-storage`、`directory-ui`、`media-cache-concurrency`、`collection`、`playlist-management` 和 `playlist-mirrors`，九组全部通过。独立存储组另覆盖 SAF 目录内同 SHA 音频跨歌曲复用、持久归属标记，以及未归属旧文档不读不删。歌单互转断言分别核对 Music 的两个身份扩展字段和其余标准字段完整往返。

桌面 Release 编译为 0 警告、0 错误；Android ARM64 发布通过签名、ABI 和 16 KB 对齐检查，最终包位于 `artifacts/android-arm64-20261117-174846-c1384aa5/`，38.87 MiB。本轮未安装或进行 Android 实机验证。上文已记录的 `audio-download-progress` 基线超时不在本轮修复范围。

## 歌曲完整详情与在线互动

2026-11-17，手机歌曲详情增加半屏、全屏两档。点击或轻上滑迷你播放器先打开半屏，继续上拉或点击展开按钮进入全屏；全屏收起返回半屏。桌面右侧增加展开按钮，在原内容区打开完整详情并保留底部播放器。面板以同一进度控制可打断动画，触摸取消回到原档位；评论的滚动与输入不抢占抽屉手势。

完整详情提供歌曲信息、MajNet 点赞与撤销、评论与回复、本人删除、评论点赞，并预留 `ChartPreviewHost`。点赞独立于收藏。请求根据歌曲所属接入点绑定当前会话，Guest 只读，离线不发起请求。评论按页加载，追加时保留既有行与滚动位置；切歌、换账号、关闭详情取消旧请求，迟到结果不替换新歌曲或清空新草稿。

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- social-network social-presentation song-details-layout mobile-player-gesture mobile-safe-area source-transport auth-boundaries collection-network independent-media-storage
```

九组全部通过。`social-network` 使用真实工作区与模拟 HTTP 核对路由、JSON、分页、来源身份、本人删除及会话切换；`social-presentation` 故意让旧请求忽略取消，核对旧读写隔离、失败草稿保留与追加评论稳定性。`song-details-layout` 渲染桌面 1100×800、最小窗口 720×540、手机 390×844 和模拟键盘挤压后的 390×420 视口，使用指针操作展开与收起，检查评论操作未被曲库行高裁切、输入区与发送按钮在评论页可见区域内对齐。短视口同时保留回复目标与下一页，确保评论仍可滚动阅读；刷新、播放与加载更多改用顶部图标。合成界面截图位于 `artifacts/social-details/`，已目视核对。

接口按 [MajdataNet 官方前端契约](https://github.com/TeamMajdata/MajdataNet/blob/main/src/types/interact.ts)实现；本轮公共匿名接口探测返回 HTTP 403，未使用玩家账号发表、点赞或删除真实内容，真实服务器的登录互动待用户实测。没有读取 Play 目录或数据库，也没有引入谱面渲染依赖。[谱面预览方案](docs/ChartPreview.md) 记录 MajdataView、Web 分支和 MajSimai 的源码核对及下一步路线。

桌面 Release 编译为 0 警告、0 错误，未覆盖 Debug 输出或操作用户正在运行的播放器。最终 Android ARM64 包位于 `artifacts/android-arm64-20261117-184446-c25b27c6/MajMusic-android-arm64.apk`，38.91 MiB，通过签名、单 ARM64 ABI 与 16 KB 对齐检查；本轮未安装或进行 Android 实机验证。

## 桌面三栏详情与导航动画

2026-11-17，桌面“详情与评论”入口移到右侧歌曲卡片下方。展开后，原 `LibraryDetails` 控件经过透明飞行层横移至左栏，中栏显示评论，右栏显示点赞、输入与发送；关闭后原控件移回右栏。两边不是重复创建的详情树，卡片尺寸与纵坐标保持一致，快速收起会从当前绘制位置接续。封面按钮直接选择谱面预览区域，渲染器仍按上节方案预留。

切页修复同一次导航在稍后发布内容时重复截图、重新启动横移动画的问题；空快照释放及未改变尺寸的布局更新不再重复触发绘制失效。临时截图同时限制为最长 1920 像素和约 2 Mi 像素面积，保留原来的逻辑坐标映射；125%、150%、175% 桌面卡片首帧像素尺寸检查保留。导航先给选择控件处理反馈，再合并提交最后一个目标。播放／暂停图标只交叉淡入，取消旋转、缩放及播放按钮额外的按压、标签位移动效。

分组运行以下八组，全部通过：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- mobile-player-gesture social-presentation song-details-layout surface-geometry arc-responsive arc-motion surface-motion playback-region-motion
```

`song-details-layout` 渲染 1100×800、720×540 桌面三栏以及原手机半屏、全屏和短视口。真实指针分别打开评论、进入谱面区域及返回；中栏滚动时左右栏与输入框不移动。播放 A、查看 B 时，所有详情操作保持播放曲目、音频打开次数与队列不变。`surface-motion` 使用实际渲染与 Arc 手动时钟，验证截图像素预算、延迟内容不重复绘制旧树、同一卡片的中帧、反向接续和退出释放。截图保存在 `artifacts/social-details/`，已目视检查；没有据此宣称设备上的实际帧率。

额外运行 `headless` 在原有第 114 行卡片宽高比断言失败，合成卡片为 402×203，要求宽度与两倍高度差小于 2。隔离检出改动前的 `f56a466` 后，同组在同一位置复现。该断言及卡片比例实现保持不变；对照记录位于 `artifacts/headless-baseline-evidence-f803aef9/`。

最终桌面 Release 编译为 0 警告、0 错误。Android ARM64 包为 `artifacts/android-arm64-20261117-194721-4991bd3e/MajMusic-android-arm64.apk`，38.91 MiB，通过签名、单 ARM64 ABI 与 16 KB 对齐检查；未安装或进行 Android 实机验证。

## 等宽侧栏、尺寸过渡与统一滚动槽

2026-11-17，完整详情三栏共用同一内容行，顶部与底部齐边，左右两栏采用相同的宽度范围。原歌曲卡片在横移过程中连续调整真实布局宽高，文字不做非等比拉伸；快速反向从当前尺寸与位置接续，关闭后恢复右侧原尺寸。封面与两侧内边距一同适配扩展宽度。

滚动条设置由应用全局加载，覆盖桌面、手机与独立弹出根。统一在模板外层传递 `ScrollViewer.AllowAutoHide=False`，并让内容展示器只占内容格，滚动条使用独立的列或行。列表、输入框模板原先会从父控件重新绑定自动隐藏设置，已一并处理；保留原有虚拟化、滚轮与滑块拖动。

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- scroll-gutters song-details-layout surface-motion surface-geometry mobile-player-gesture arc-responsive
```

六组全部通过。三栏检查覆盖 1100×800 和 720×540 的齐边、等宽、卡片贴合、独立评论滚动与输入区域。实际 Arc 中间帧验证尺寸位于起点与终点之间，快切位置与尺寸连续，落槽、减少动画与退出均还原控件归属。合成截图位于 `artifacts/social-details/`，已目视检查。

`scroll-gutters` 不加载 MainView 的局部样式，直接检查普通与双轴 ScrollViewer、虚拟化 ListBox、多行 TextBox、Arc Textarea 和真实 Select 弹出根。在 720／390 宽度及 125% 缩放下，内容边界不进入滚动条区域，悬停与按压不改变滚动槽；滚轮、滑块拖动及虚拟化仍有效。

最终桌面 Release 编译为 0 警告、0 错误。Android ARM64 包为 `artifacts/android-arm64-20261117-204528-b985349a/MajMusic-android-arm64.apk`，38.91 MiB，通过签名、单 ARM64 ABI 与 16 KB 对齐检查；本轮未安装或进行 Android 实机验证。

## 固定内容宽度、稳定行与切页性能

2026-11-18，音乐列表和加载占位都使用原生滚动容器预留同一条滚动槽，列数、封面准备和卡片尺寸取实际内容宽度。短页面隐藏滚动条的绘制与命中，保留其宽度；推荐页码加入后保持卡片位置。手机端移除重复提交外层宽度的路径。

主页复用已有行与歌曲卡片。`SongCardRow` 在原位置更新歌曲切片和列数，集合只对尾部增删发送一次范围通知；页码包装层转发原集合事件。`BrowseCardListBox` 为歌曲行和页码使用不同回收键，行容器通过模板优先级保留原 `SongCardRowView`，继续调用框架的选中状态准备与清理。隐藏、回收和分离会清空旧卡片的数据及资源兴趣，恢复时只实例化当前可见行；未激活时按几何关系保留行高。

已准备的首屏在发布行通知前标记就绪，跳过同一次输入内的占位创建与撤回。普通页面模板和最多 32 张静态占位在空闲帧准备，导航动画期间推迟工作；隐藏占位从可视树分离。图标按钮关闭额外的标签位移与按压缩放，封面快捷播放滑出后的坐标复位先隐藏，再在原位置淡入。

原生测量使用独立可见 Win32／Skia 窗口、125% 缩放、600 首生成歌曲、20 个歌单和生成封面；HTTP 与凭据调用均为 0。相同测量流程记录输入调用和紧随其后的显式布局，旧实现与最终实现各运行两轮：

| 场景 | 修改前调用＋布局 | 修改后调用＋布局 | 修改后首次布局 |
| --- | ---: | ---: | ---: |
| 首切歌单 | 36.65–50.78 ms | 9.66–10.04 ms | 1.41–1.58 ms |
| 首切曲库 | 39.00–44.53 ms | 6.19–6.23 ms | 0.53–0.56 ms |

最终两轮共八次主页模式切换均无集合 Reset／Replace、无新增歌曲卡片、复用原来的九张可见卡片，占位创建与借用增量均为 0；仅对列表尾部增删。最大 UI 心跳间隔为 11.42–18.23 ms。四个未打开的桌面页预热成功，导航前 `Loaded` 次数均为 0；目录首次布局从初始基线约 37.55 ms 降至最终约 5 ms，设置页从约 21.68 ms 降至约 3 ms。

工具与复测方法见 [NativeNavigation](Tools/Diagnostics/NativeNavigation/README.md)。原始结果位于 `artifacts/native-navigation-row-before-a-76caee3b/`、`artifacts/native-navigation-row-before-b-070d982e/`、`artifacts/native-navigation-slots-quiet-a-4dc5e161/` 和 `artifacts/native-navigation-slots-quiet-b-d75b97ca/`；最后一处包含 `final-two-run-summary.csv`。这些是 UI 调度与布局测量，帧回调不代表 GPU 呈现时间或 Android 实机帧率。

以下 21 组共享逻辑与绘制回归全部通过：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- mobile-player-gesture song-details-layout scroll-gutters arc-responsive surface-motion browse-loading browse-placeholder-lifecycle browse-entry browse-rows song-card-row-reuse recommendation-paging-layout cover-play-feedback playback-region-motion panel-paging cover-preload catalog-memory presentation-retention surface-geometry arc-motion context-actions context-menus-layout
```

新增检查覆盖真实列表 Reset 后保留控件、正常切换无 Reset、同一行更新标题与实际封面像素、旧快捷播放取消、隐藏与分离时零新歌曲索引、重新激活只读最新切片、小型密度、手机列数、End 键、页码回收隔离，以及 24,000 首曲库的首屏扩展。图标检查包含内部 Path 与所有祖先变换，尺寸过渡覆盖动画末帧。冷加载用新到达的歌单阻塞查询；已预热目标则验证零占位借用。比例检查保留封面正方形断言，允许原生布局舍入的一物理像素误差。

桌面 Release 编译为 0 警告、0 错误。最终 Android ARM64 包为 `artifacts/android-arm64-20261118-000721-1cdbff34/MajMusic-android-arm64.apk`，38.92 MiB，通过签名、单 ARM64 ABI 与 16 KB 对齐检查；未安装或进行 Android 实机验证。

## 常驻主页与快速反向切换

推荐、歌单、曲库各持有独立的列表状态和卡片视图。每页预载前 25 首的卡片数据，已解码的封面在页面离开视口后仍保留。同一条横移轴从当前画面重新定向；暖切换不截取旧页、不重查曲库、不替换列表，也不会在中途补齐列表尾部。只在页面内容实际改变时更新数据与滚动位置。

[原生诊断](Tools/Diagnostics/NativeNavigation/README.md)在 Windows 桌面窗口和 420 逻辑像素宽的窄屏窗口各发送 12 次鼠标点击，其中 9 次打断上一段动画。先悬停实际卡片播放图标，底部保持播放状态。两种尺寸都在首切前准备了每页 25 张封面；切换中卡片、封面引用未替换，首屏控件持续挂载。固定图标测得位移为 0，封面图标只随整页横移。封面快捷播放的指针丢失用例另行检查，确保从当前姿态沿原轨迹返回。

与前一版按相同步骤比较，重复切换的输入到选中反馈约由桌面 15–22 ms 降至 4–6 ms、窄屏 8–17 ms 降至 3–5 ms。数字是选中反馈耗时；窄屏使用 Windows 上的移动布局。报告见 `artifacts/native-navigation-rapid-resident-final/rapid-report.json` 和 `artifacts/native-navigation-rapid-mobile-resident-final/rapid-report.json`。

26 组回归通过，覆盖快速切页图标、快捷播放回退、滚动槽、搜索恢复、推荐分页、加载占位和曲库延迟实例化。桌面 Release 零警告、零错误。Android ARM64 包为 `artifacts/android-arm64-20261118-022810-eabd3590/MajMusic-android-arm64.apk`，38.92 MiB，通过签名、ABI 与对齐检查。

## MajView 谱面预览

使用官方 WebGL 构建和 MajMusic 的实际预览页面，在 Windows Chrome 的 1080 和 420 像素窗口播放生成的谱面、MP3 与带时间码的 H.264 PV。确认难度 7 → 5 → 2 按原编号切换，三档的 PV 均显示并静音，无 PV 时显示原封面。暂停后跳到 2.5 秒，PV 时间约为 2.500–2.504 秒。流速 2.5 和 8.0 的落点时间与音乐速率不变，音符位置不同；Web Audio 记录到测试谱面的首个打击音在音乐起点后 260 和 268 ms 调度，谱面预期 250 ms。

从页内表单应用设置后，使用 MajView 的原生菜单读回音符流速 8、触摸流速 9.5、打击音 −24 dB。同一难度重新加载后 PV 仍显示，关闭后本机预览地址失效。报告和截图在 `artifacts/majview-preview-fixture/browser/session/`、`fresh-views/` 和 `settings-final/`。

14 组回归通过，覆盖官方构建的校验和离线复用、谱面与 PV 按需读取、旧接入点兼容、账号隔离、本机预览的 Range 请求与关闭、预览前暂停主播放器及保留队列、选曲中断、详情布局。桌面 Release 编译为 0 警告、0 错误；安卓只做 ARM64 包构建与产物校验。

## MajView 内嵌谱面框

Windows 原生窗口使用实际的 MajMusic 详情、WebView2 和生成的谱面、音乐、PV。点击详情框内的加载按钮后，预览控件的全部边界都在中央框内；调整窗口尺寸后，控件随框扩展，画面保持正方形，未出现页面滚动条。实测 7 → 5 → 2 → 7 切换、音乐与打击音、PV 静音及流速、音效设置。点击预览关闭按钮或切回评论后，内嵌视图移除，本机预览会话结束。未启动外部浏览器。

7 组回归通过，覆盖谱面资源、运行时、预览会话、详情布局、切页动效和手机播放器。Windows x64 便携包和 Android ARM64 包已生成；Android 签名、ABI 与 16 KB 对齐校验通过，未做真机测试。

## 难度页签与满幅预览

Windows 原生窗口用测试谱面验证「评论／2／5／7」页签。点击难度后直接在原框加载；从播放中的难度切到另一难度时，WebView 与会话继续复用，新谱面等待手动播放。来回切换记录到页签滑块的中间位置和谱面入场动画，PV 和打击音继续工作。左上角齿轮应用流速和音效后保留当前难度，不自动播放。

两次窗口尺寸测量中谱面正方形填满可用区域的较短一边，预览内没有常驻工具栏或滚动条。420 像素宽的 Windows 窄屏布局也通过同一流程。实测记录见 `artifacts/majview-tabs-20261117195317/` 和 `artifacts/majview-tabs-compact-20261117195555/`。12 组回归通过，覆盖谱面读取、预览会话、详情布局、手机手势及切页动效。
