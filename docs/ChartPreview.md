# MajView 谱面预览

在歌曲详情打开「谱面预览」，选择是否播放背景 PV，然后打开 MajView。首次打开按需安装约 49 MB 的官方渲染器；安装后，本地歌曲和已缓存的预览资源可离线打开。难度选项在页面顶部，音符流速、触摸流速、音乐和各类音效音量在「流速与音效」中设置。应用设置会从头加载当前难度。背景遮罩和偏移仍可在 MajView 画面内调整。

预览当前在系统浏览器里运行。MajMusic 暂停原来的播放，保留队列与进度；MajView 统一管理预览里的音乐、打击音、谱面和静音 PV。关闭预览或在 MajMusic 继续播放会结束该预览会话。

## 实现

- 本地曲读取原目录的 `maidata.txt`、音轨、封面和可选的 `pv.mp4`、`mv.mp4` 或 `bg.mp4`；不改动原文件。在线曲从该歌曲所属接入点读取，沿用 Music 的会话和缓存，勾选 PV 时才读取视频。
- 使用 [MajdataNet 的正式 WebGL 构建（e9db7b4）](https://github.com/TeamMajdata/MajdataNet/tree/e9db7b4b431954f2e9fefa7745d3650b93fc4671/public/WebGLBuild)。运行时文件校验后放入 `Runtime/MajView`，不打进桌面或 Android 包。渲染器源码见 [MajdataView_web Unity 6 分支（7b7addc）](https://github.com/TeamMajdata/MajdataView_web/tree/7b7addc468f41786e2ec8672406238befeb8f86d)（GPLv3）。
- 浏览器只读取当前预览的本机临时地址。MajMusic 将原始谱面、音轨、封面和可选 PV 交给 [上游的五行消息协议](https://github.com/TeamMajdata/MajdataNet/blob/e9db7b4b431954f2e9fefa7745d3650b93fc4671/src/components/song/Majdata.tsx)。难度编号直接对应原 `inote_1` 至 `inote_7`，不会把稀疏的难度重新编号。
- 切难度时重新打开同一预览页的渲染实例，复用媒体和运行时缓存。同一实例重复切难度会让上游 PV 纹理变黑；重新创建能稳定显示 PV，并保留本次预览内的流速和音效设置。

## 验证范围

Windows Chromium 使用生成的 MP3、带可见时间码的 H.264 PV、封面及 Simai 谱面。覆盖 2／5／7 难度、无 PV 回落封面、暂停和跳转、不同流速下同一时间的音符位置、打击音及 PV 静音。基于 MajdataView 的视频时间轴从音轨时间同步；MajdataPlay 扩展的 `&mv_seek`、`&mv_wait` 目前没有应用。已验证的音轨格式为 MP3。

下一步是用各平台的嵌入浏览器承载同一个预览页，再在真机验证视频解码、跳转和音轨切换。继续使用官方渲染器可保留完整音符和音效规则；如果改为跟随 MajMusic 主播放器的实时预览，需要单独实现时钟同步和播放权交接。
