# DJI-RTMP-OBS-Windows

把 DJI 运动相机（Osmo Action 6 / Action 5 Pro 等）的画面通过局域网 **RTMP 无线推流**到 Windows 电脑，交给 OBS 当一个机位。macOS 版 [renqix/DJI-RTMP-OBS](https://github.com/renqix/DJI-RTMP-OBS) 的 Windows 实现。

## 原理

```
相机(H.265 RTMP 推流) → 局域网 → 本机 MediaMTX(1935) → 转 RTSP(8554) → OBS「媒体源」
```

**为什么必须中转**：DJI 设备默认推 H.265，OBS 的 RTMP 输入不认，表现是"有声音、黑屏"；走 RTSP 才能正常解码。推流地址（rtmp://）和接收地址（rtsp://）**不能混用**。

## 快速开始

1. **编译**：双击 `scripts/build.bat` → 生成 `TrayApp.exe`（用 Windows 自带的 .NET 4.8 编译器，无需安装任何开发环境）
2. **安装**：右键以管理员身份运行 `scripts/install.ps1`（自动下载 MediaMTX、校验 SHA-256、添加防火墙规则；网络不佳时先开代理/加速器）
3. **启动**：双击 `TrayApp.exe` → 托盘图标 → 「启动 MediaMTX」→ 点击推流地址自动复制
4. **相机**：DJI Mimo 连接相机 → 直播 → RTMP → 粘贴推流地址 → 选与电脑同一 5GHz Wi-Fi → 开始直播
5. **OBS**：添加「媒体源」→ 输入 `rtsp://127.0.0.1:8554/live/camera1` → 勾选「断开时重新连接」→ 「输入格式」留空

## 常见坑

- **相机填 `rtmp://`，OBS 填 `rtsp://`**，两个地址不要混用
- 用手机热点供网时：**先进入 Mimo 直播配置页、再打开热点**，否则相机连上热点后会顶掉与 Mimo 的直连
- 换网络后电脑 IP 变了：托盘菜单点「刷新地址」即可；OBS 侧是回环地址（127.0.0.1），永远不用改
- 本配置未启用鉴权，请在可信局域网内使用

## 目录结构

```
config/mediamtx.yml   精简配置（只开 RTMP/RTSP/API，关闭 HLS/WebRTC/metrics）
scripts/              install.ps1（下载+防火墙）与 build.bat（编译）
src/TrayApp.cs        托盘工具源码（.NET Framework 4.8）
```

## 致谢

- [renqix/DJI-RTMP-OBS](https://github.com/renqix/DJI-RTMP-OBS)：macOS 原版，提供了架构思路与 H.265 黑屏的踩坑记录
- [bluenviron/mediamtx](https://github.com/bluenviron/mediamtx)：流媒体服务器

## License

MIT
