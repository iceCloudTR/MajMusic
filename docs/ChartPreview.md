# MajView 谱面预览

在歌曲详情打开「谱面预览」，选择是否播放背景 PV，然后点「加载谱面预览」。首次加载按需下载约 49 MB 的官方渲染器；安装后，本地歌曲和已缓存的预览资源可离线打开。难度选项在预览框上方，音符流速、触摸流速、音乐和各类音效音量在「流速与音效」中设置。应用设置会从头加载当前难度。背景遮罩和偏移仍可在 MajView 画面内调整。

预览就在歌曲详情的中央框内运行。MajMusic 暂停原来的播放，保留队列与进度；MajView 统一管理预览里的音乐、打击音、谱面和静音 PV。切回评论、关闭详情或在 MajMusic 继续播放会结束预览。

## 实现

- 本地曲读取原目录的 `maidata.txt`、音轨、封面和可选的 `pv.mp4`、`mv.mp4` 或 `bg.mp4`；不改动原文件。在线曲从该歌曲所属接入点读取，沿用 Music 的会话和缓存，勾选 PV 时才读取视频。
- 使用 [MajdataNet 的正式 WebGL 构建（e9db7b4）](https://github.com/TeamMajdata/MajdataNet/tree/e9db7b4b431954f2e9fefa7745d3650b93fc4671/public/WebGLBuild)。运行时文件校验后放入 `Runtime/MajView`，不打进桌面或 Android 包。渲染器源码见 [MajdataView_web Unity 6 分支（7b7addc）](https://github.com/TeamMajdata/MajdataView_web/tree/7b7addc468f41786e2ec8672406238befeb8f86d)（GPLv3）。
- 预览框使用系统内嵌网页控件，只加载当前预览的本机临时地址。MajMusic 将原始谱面、音轨、封面和可选 PV 交给 [上游的五行消息协议](https://github.com/TeamMajdata/MajdataNet/blob/e9db7b4b431954f2e9fefa7745d3650b93fc4671/src/components/song/Majdata.tsx)。难度编号直接对应原 `inote_1` 至 `inote_7`。
- 切难度时重新打开同一预览页的渲染实例，复用媒体和运行时缓存。同一实例重复切难度会让上游 PV 纹理变黑；重新创建能稳定显示 PV，并保留本次预览内的流速和音效设置。

## 验证范围

Windows WebView2 实测预览在详情框内加载，验证难度切换、背景 PV、音乐、打击音和关闭释放。预览视频跟随 MajView 音轨时间；MajdataPlay 扩展的 `&mv_seek`、`&mv_wait` 目前没有应用。已验证的媒体格式是 MP3 和 H.264 MP4。

桌面使用 [Avalonia 原生 WebView](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)：Windows 需要 WebView2，macOS 使用 WKWebView，Linux 使用 WPE WebKit 或 WebKitGTK。Android 使用系统 WebView，退到后台时关闭谱面预览。macOS、Linux 和 Android 实机尚未验证。
