# 开发验证

运行完整默认回归，或只运行与改动相关的测试组。界面测试使用 Avalonia 离屏窗口，曲库、账号和网络接口使用测试数据。

```powershell
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release
dotnet run --project MajMusic.Tests/MajMusic.Tests.csproj -c Release -- song-details-layout scroll-gutters majview-runtime majview-session
```

其他专项检查：

| 测试组 | 验证范围 |
| --- | --- |
| `catalog-memory presentation-retention` | 大曲库、封面解码、浏览页持有范围 |
| `independent-media-storage document-storage` | 本地目录、下载归属、迁移和文档授权 |
| `source-transport auth-boundaries cloud-collection-sync` | 网络隔离、登录状态和云端收藏 |
| `surface-motion mobile-player-gesture rapid-nav-icons` | 过渡、触摸和快速切页 |
| `desktop-audio desktop-integration desktop-metadata desktop-audio-controls` | LibVLC 播放、参数读取和音量控制 |
| `native-vault` | 系统凭据存储 |
| `desktop-output` | 默认设备输出测试音 |

Android 冷启动、签名和凭据验证见 [开发工具](Tools/README.md)。需要在对应设备上确认后台播放、系统 WebView 和本地目录授权。

MajView 的运行方式见 [谱面预览](docs/ChartPreview.md)。Windows 实际切页记录可用 [NativeNavigation](Tools/Diagnostics/NativeNavigation/README.md) 重测。
