# 开发验证

## 构建与回归

```sh
dotnet build MajMusic.sln
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj
```

默认回归覆盖数据存储、目录类型、收藏夹格式、账号选择与自动登录、网络与缓存、播放队列、音频设置，以及界面交互。

可在命令末尾指定测试组：

```sh
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -- playback account-ui directory-ui audio-settings-ui headless
```

界面测试使用真实 Avalonia 控件验证均分按钮、居中播放控制、封面卡片、虚拟化滚动和键盘选择，并生成深色、浅色、紧凑窗口和缩放渲染，结果位于 `.work/`。

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
