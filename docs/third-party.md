# 第三方来源

- PICO Motion Tracker Sample：`https://github.com/Pico-Developer/PICOMotionTrackerSample-Unity`，基础提交 `4a74a91c4fef6794c8d0d6577c5db3c463bee09e`。覆盖层中的部分 Unity 配置与原场景结构来自该基础工程。原 MIT 文本保留于 `third-party/PICO-sample-MIT.txt`。
- PICO Unity Integration SDK 3.4.0：未打包，其源码、原生库和企业支持遵循提供方条款，需要另外安装。
- NatCorder：未打包，其运行时与 Android 编码插件需要另外安装；`EgoMp4Recorder.cs` 是调用该后端的项目包装器。
- Unity XR Hands / XR Management 和其他 UPM 包：通过清单解析，各遵循其自己的许可证。
- Python 工具依赖 NumPy、Pillow、imageio-ffmpeg：通过 requirements 安装；仓库不附带 Python 环境和 FFmpeg 二进制。

本项目的新增源码尚未指定公开开源许可证。整理到 GitHub 不自动授予第三方任意使用权；正式公开发布前由项目所有者决定新增代码的许可证。依赖、官方 SDK 与论文的权利不因本仓库而改变。
