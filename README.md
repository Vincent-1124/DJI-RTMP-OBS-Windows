# DJI-RTMP-OBS-Windows

把 DJI 运动相机（Osmo Action 6 / Action 5 Pro 等）的画面通过局域网 **RTMP 无线推流**到 Windows 电脑，交给 OBS 当一个机位。macOS 版 [renqix/DJI-RTMP-OBS](https://github.com/renqix/DJI-RTMP-OBS) 的 Windows 实现。

## 原理

```
相机(H.265 RTMP 推流) → 局域网 → 本机 MediaMTX(1935) → 转 RTSP(9554) → OBS「媒体源」
```

> RTSP 端口用 **9554** 而不是常见的 8554：Windows 的 Hyper-V/WSL 保留端口段经常覆盖 8475–8574，会导致绑定失败（WSAEACCES）。

**为什么必须中转**：DJI 设备默认推 H.265，OBS 的 RTMP 输入不认，表现是"有声音、黑屏"；走 RTSP 才能正常解码。推流地址（rtmp://）和接收地址（rtsp://）**不能混用**。

## 快速开始

**首次（向导引导，一次完成）**：
1. 双击 `scripts/build.bat` → 自动编译并启动应用（用 Windows 自带的 .NET 4.8 编译器，无需安装任何开发环境）
2. 跟随安装向导：开始配置 → 下载并安装（自动 SHA-256 校验、解压、防火墙规则、启动服务，期间弹一次 UAC 授权点"是"）→ 开始使用

**以后每次直播（双击即用）**：
1. 双击 `DJI-RTMP-OBS.exe`（服务自动启动，窗口可最小化到托盘）
2. **相机**：DJI Mimo 连接相机 → 直播 → RTMP → 粘贴窗口里的推流地址（点"复制"）→ 选与电脑同一 5GHz Wi-Fi → 开始直播
3. **OBS**：添加「媒体源」→ 输入 `rtsp://127.0.0.1:9554/live/camera1` → 勾选「断开时重新连接」→ 「输入格式」留空

窗口里"流状态"显示 **● 直播中** 即表示相机画面已到达电脑。

## 常见坑

- **相机填 `rtmp://`，OBS 填 `rtsp://`**，两个地址不要混用
- 用手机热点供网时：**先进入 Mimo 直播配置页、再打开热点**，否则相机连上热点后会顶掉与 Mimo 的直连
- 换网络后电脑 IP 变了：托盘菜单点「刷新地址」即可；OBS 侧是回环地址（127.0.0.1），永远不用改
- 本配置未启用鉴权，请在可信局域网内使用

## 目录结构

```
config/mediamtx.yml   精简配置（只开 RTMP/RTSP/API，关闭 HLS/WebRTC/SRT/MoQ/metrics）
scripts/build.bat     编译脚本（.NET Framework 4.8 / x64）
src/TrayApp.cs        应用源码（GUI + 一键安装 + 服务管理 + 托盘）
```

## 致谢

- [renqix/DJI-RTMP-OBS](https://github.com/renqix/DJI-RTMP-OBS)：macOS 原版，提供了架构思路与 H.265 黑屏的踩坑记录
- [bluenviron/mediamtx](https://github.com/bluenviron/mediamtx)：流媒体服务器

## License

MIT
