# MajView 谱面预览

歌曲详情顶部会列出「评论」和当前谱面可用的难度。点任一难度，谱面直接加载到中央框；点画面下方的播放按钮才开始。首次按需下载约 49 MB 的官方渲染器，之后本地和已缓存的谱面可离线打开。

左上角齿轮提供背景 PV、音符流速、触摸流速、音乐和各类音效音量。应用设置会重新加载当前难度，并等待手动播放。背景遮罩和偏移仍可在 MajView 画面内调整。

预览就在歌曲详情的中央框内运行。MajMusic 暂停原来的播放，保留队列与进度；MajView 统一管理预览里的音乐、打击音、谱面和静音 PV。切回评论、关闭详情或在 MajMusic 继续播放会结束预览。

## 实现

- 本地曲读取原目录的 `maidata.txt`、音轨、封面和可选的 `pv.mp4`、`mv.mp4` 或 `bg.mp4`；不改动原文件。在线曲从该歌曲所属接入点读取，沿用 Music 的会话和缓存，勾选 PV 时才读取视频。
- 使用 [MajdataNet 的正式 WebGL 构建（e9db7b4）](https://github.com/TeamMajdata/MajdataNet/tree/e9db7b4b431954f2e9fefa7745d3650b93fc4671/public/WebGLBuild)。运行时文件校验后放入 `Runtime/MajView`，不打进桌面或 Android 包。渲染器源码见 [MajdataView_web Unity 6 分支（7b7addc）](https://github.com/TeamMajdata/MajdataView_web/tree/7b7addc468f41786e2ec8672406238befeb8f86d)（GPLv3）。
- 预览框使用系统内嵌网页控件，只加载当前预览的本机临时地址。MajMusic 将原始谱面、音轨、封面和可选 PV 交给 [上游的五行消息协议](https://github.com/TeamMajdata/MajdataNet/blob/e9db7b4b431954f2e9fefa7745d3650b93fc4671/src/components/song/Majdata.tsx)。难度编号直接对应原 `inote_1` 至 `inote_7`。
- 打开详情时只读取谱面以列出真实难度。点难度后才准备音频、PV 和渲染器。切换时复用内嵌控件、媒体和同一会话，只重新加载 MajView 渲染实例，以保持 PV 纹理正常并沿用本次预览的设置。页签滑块和谱面入场随切换方向过渡。

## 验证范围

Windows WebView2 实测点难度直接加载、切换后保持暂停、PV 与打击音、流速和关闭释放。预览会用满中央框的较短一边，保持正方形。MajdataPlay 扩展的 `&mv_seek`、`&mv_wait` 目前没有应用。已验证的媒体格式是 MP3 和 H.264 MP4。

桌面使用 [Avalonia 原生 WebView](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)：Windows 需要 WebView2，macOS 使用 WKWebView，Linux 使用 WPE WebKit 或 WebKitGTK。Android 使用系统 WebView，退到后台时关闭谱面预览。macOS、Linux 和 Android 实机尚未验证。
