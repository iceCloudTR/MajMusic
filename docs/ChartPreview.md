# 谱面预览后续方案

本轮仅保留谱面预览的位置，不实现渲染器，也不改变音乐播放。

后续采用独立谱面解析与绘制组件。主时钟来自现有 `PlaybackService`，预览跟随同一首歌的播放、暂停和跳转，不启动第二套音乐播放器。谱面由明确选择的本地文件或所属接入点提供，沿用 Music 的存储和账号边界。

## 已核实的上游

- [MajdataView，9406e3e](https://github.com/TeamMajdata/MajdataView/tree/9406e3e5b171449e434256f504b5b42d4d21eab3)：Unity 2022.3.15f1，[GPLv3](https://github.com/TeamMajdata/MajdataView/blob/9406e3e5b171449e434256f504b5b42d4d21eab3/LICENSE)。[HttpHandler](https://github.com/TeamMajdata/MajdataView/blob/9406e3e5b171449e434256f504b5b42d4d21eab3/Assets/Scripts/HttpHandler.cs) 接收外部控制，[JsonDataLoader](https://github.com/TeamMajdata/MajdataView/blob/9406e3e5b171449e434256f504b5b42d4d21eab3/Assets/Scripts/JsonDataLoader.cs) 读取时间点和音符；[AudioTimeProvider](https://github.com/TeamMajdata/MajdataView/blob/9406e3e5b171449e434256f504b5b42d4d21eab3/Assets/Scripts/AudioTimeProvider.cs) 从外部起始时间、位置和速度推进。绘制依赖 Unity 场景、Prefab、SpriteRenderer、动画和材质。
- [MajdataView_web，9653126](https://github.com/TeamMajdata/MajdataView_web/tree/9653126c960a8b29830cbb84e5e5b2f4bbbc3965)：Unity 2021.3.21f1，[GPLv3](https://github.com/TeamMajdata/MajdataView_web/blob/9653126c960a8b29830cbb84e5e5b2f4bbbc3965/LICENSE)。增加 maidata 解析和 Unity AudioSource；当前资源入口仍与旧赛事服务耦合，不能直接作为通用预览控件。
- [MajSimai，a0a0c64](https://github.com/TeamMajdata/MajSimai/tree/a0a0c64a3e38ed855201acc7d8ca4b846db2f043)：[项目配置](https://github.com/TeamMajdata/MajSimai/blob/a0a0c64a3e38ed855201acc7d8ca4b846db2f043/MajSimai.csproj) 支持 net10.0、netstandard2.1，许可证为 GPL-3.0-or-later，可作为独立解析层。

优先验证 MajSimai 加 [Avalonia/Skia 自定义绘制](https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering)，逐步迁移音符轨迹和视觉规则。皮肤、字体和插件在实际引用时分别记录许可与署名。

整套 Unity 嵌入作为备选：[Unity as a Library](https://docs.unity3d.com/2022.3/Documentation/Manual/UnityasaLibrary.html) 的标准移动方案限定全屏，卸载后仍保留部分运行时内存；Web 分支当前使用的 [Unity 2021.3 WebGL](https://docs.unity3d.com/cn/2021.3/Manual/webgl-browsercompatibility.html) 不支持移动设备。直接嵌入不足以满足统一的小型预览控件目标。

## 最小下一阶段

1. 用一个本地 maidata 建立解析、时间线和固定比例绘制面，先实现 Tap/Hold，再补 Slide、Touch、WiFi。
2. 接入现有播放时钟，验证暂停、跳转、换歌和关闭释放；预览开关保留当前播放队列。
3. 用同一组件验证桌面和 Android 的尺寸、帧率与视觉，再扩展 iOS。
4. 最后接入在线谱面读取与缓存，复用所属接入点会话；不读取其他应用目录或数据库。

上述为源码研究和实施顺序，尚未验证实际渲染与平台运行效果。
