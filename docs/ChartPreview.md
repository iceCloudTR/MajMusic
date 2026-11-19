# MajView 谱面预览

从简介卡片点「歌曲详情」，再选难度即可打开谱面。手机详情收起、半开或展开时都可以操作底部播放器。看谱时底栏显示谱面时间，打开评论或收起详情后显示歌曲时间。同曲加载或切换难度时，底栏保留已知时长和当前位置。加载中也可以拖动，谱面准备好后会定位到最后选的位置。时长待读取的曲目显示加载轨道。临时看另一首歌时，返回后仍能接着听原来的队列。

- 左键在前 3 秒切到上一首，从第 3 秒起重播当前歌曲。中键播放或暂停，右键切下一首。切换难度、进出谱面或进入全屏时，播放器停在当前位置。
- 普通详情将锁头放在左上角，全屏与齿轮放在右上角。左下角显示 TAP、HOLD、SLIDE 的「当前 / 总数」，右下角显示 TOUCH、BREAK。当前数随播放和拖动更新，长押和滑条轨迹计到尾判。点齿轮打开设置菜单，可以调整音符流速、触摸流速、播放速度、背景遮罩、打击音偏移和打击音量。「保存」会写入应用本地设置。
- 桌面全屏练习铺满当前窗口的内容区，Android 使用沉浸式全屏并允许随设备旋转。练习时间轴显示音频波形和分类型音符密度。拖动任一轨道可定位。竖屏时轨道在底部，横屏时在右侧。进入和退出时圆盘先收拢再展开，途中可以点返回。系统减少动画时直接切换。返回详情会保留当前难度和暂停位置。
- 普通详情与全屏均可锁定触控。长按锁头半秒，进度动画走完后恢复操作。

## 实现

- 本地曲读取原目录的 `maidata.txt`、音轨、封面和可选的 `pv.mp4`、`mv.mp4` 或 `bg.mp4`；不改动原文件。在线曲从该歌曲所属接入点读取，沿用 Music 的会话和缓存，勾选 PV 时才读取视频。
- 渲染器发布前会核对文件校验值，写入桌面包的 `MajView` 子目录和 APK 的 `assets/MajView`。[定制版的构建方法](../Tools/README.md#majview-构建资源)基于 [MajdataView_web 源码（7b7addc，GPLv3）](https://github.com/TeamMajdata/MajdataView_web/tree/7b7addc468f41786e2ec8672406238befeb8f86d)，也可对照 [MajdataNet 的 WebGL 构建（e9db7b4）](https://github.com/TeamMajdata/MajdataNet/tree/e9db7b4b431954f2e9fefa7745d3650b93fc4671/public/WebGLBuild)。
- 预览框使用系统内嵌网页控件，只加载当前预览的本机临时地址。MajMusic 将原始谱面、音轨、封面和可选 PV 交给 [上游的五行消息协议](https://github.com/TeamMajdata/MajdataNet/blob/e9db7b4b431954f2e9fefa7745d3650b93fc4671/src/components/song/Majdata.tsx)。本机会话通过带预览编号的控制请求交换播放意图和音轨时钟，新旧难度的回报独立。难度编号直接对应原 `inote_1` 至 `inote_7`。
- 打开详情时只读取谱面以列出真实难度。点难度后才准备音频、PV 和渲染器。切换时复用内嵌控件、媒体和同一会话，只重新加载 MajView 渲染实例，以保持 PV 纹理正常并沿用本次预览的设置。页签滑块和谱面入场随切换方向过渡。

谱面、打击音和 PV 共用歌曲的播放位置。变速从选定的歌曲时间开始，拖动或进出全屏后会显示该位置的视频帧。

## 验证范围

Windows WebView2 实测点难度直接加载、切换后保持暂停、PV 与打击音、流速和关闭释放。定制版还检查了窗口缩放，以及横向和窄屏预览中谱面与控件的布局。MajdataPlay 扩展的 `&mv_seek`、`&mv_wait` 目前没有应用。已验证的媒体格式是 MP3 和 H.264 MP4。

桌面使用 [Avalonia 原生 WebView](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)：Windows 需要 WebView2，macOS 使用 WKWebView，Linux 使用 WPE WebKit 或 WebKitGTK。Android 使用系统 WebView，退到后台时关闭谱面预览。macOS、Linux 和 Android 实机尚未验证。
