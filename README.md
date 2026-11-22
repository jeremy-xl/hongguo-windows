# 红果 TV · Windows（Avalonia）

C# + Avalonia 11 的 Windows 客户端，复用当前 Kotlin APK 的同一套内容接口与播放链路。仅新增 `desktop/` 目录，未改动仓库其他部分。

## 结构

- `src/HongguoTV.Core`：从 `kotlin-tv/core` 移植的纯 C# 层
  - `Crypto/`：请求签名（`Signer`、`PlayerSignature`、`AesV3`、V5 常量）、匿名设备注册（`DeviceRegister`）
  - `ContentRepository.cs`：首页、搜索（App 接口 + 官网兜底 + 联想词）、详情、播放模型（v5 / video_tab / 单集三路回退）
  - `MediaCrypto.cs` / `RemoteVideo.cs`：播放 URL 解密、MP4 样本解密、HTTP Range 直连
  - `LocalMediaServer.cs`：仅本机回环的 Range 代理，把解密字节提供给 LibVLC
  - `LibraryStore.cs`：本地收藏与续播历史（`%LOCALAPPDATA%/HongguoTV.Desktop/library.json`）
- `src/HongguoTV.Desktop`：Avalonia 界面（推荐、搜索、详情选集、清晰度/倍速、播放器、收藏、历史）

## 构建与运行

```powershell
dotnet build desktop/HongguoTV.slnx
dotnet run --project desktop/src/HongguoTV.Desktop/HongguoTV.Desktop.csproj
```

需要 .NET 8 SDK。首次运行会自动从 NuGet 还原 Avalonia、LibVLCSharp 与 VideoLAN.LibVLC.Windows。

## 与 Kotlin 版的对应关系

| Kotlin | C# |
| --- | --- |
| `Signer.kt` | `Crypto/Signer.cs` |
| `PlayerSignature.kt` / `Argus.kt` | `Crypto/PlayerSignature.cs` |
| `AesV3.kt` / `V5Constants.kt` / `VendorConstants.kt` | `Crypto/` 同名文件 |
| `DeviceRegister.kt` / `PlaybackDeviceSession.kt` | `Crypto/DeviceRegister.cs` / `ContentRepository.GetProfileAsync` |
| `ContentRepository.kt` / `PlaybackModels.kt` / `MediaCrypto.kt` | 同名文件 |
| `RemoteVideo.kt` + Media3 `VideoDataSource` | `RemoteVideo.cs` + `LocalMediaServer.cs` + LibVLC |

签名层已用 Kotlin 仓库的四组 oracle 逐项校验一致：`signing.json`（12 行）、`signing-v5-carrier.json`（4 行）、`signing-v5-f13.json`（32 行）、`player-signing.json`（2 行）。

## 已知边界

- VLC 无法直接接 Kotlin 版的自定义加密 DataSource，因此 Windows 端用本机回环代理暴露解密后的字节；不监听外网地址，视频流量不落盘。
- 登录、云端历史/收藏、扫码、评论弹幕、应用内更新、跨季连播等功能尚未移植，当前覆盖浏览、搜索、详情、播放与本地收藏/历史。
- 平台接口可能随时变化；遇到播放失败请查看窗口底部状态栏提示。
