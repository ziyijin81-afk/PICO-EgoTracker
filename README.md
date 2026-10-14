# PICO EgoTracker V1.0

第一视角 RGB、头部 6DoF、左右手各 26 个关节，以及可选的双腕 Motion Tracker 数据采集与实验性融合。

这是本地实现的 EgoKit 风格采集扩展，不是 EgoKit 官方源码，也不是 PICO 官方手追算法实现。没有额外的腕部相机。PICO 提供原始手追，应用通过 Unity XR Hands 读取数据；Tracker 融合在本项目中实现。

## 功能

- 音量 `+` 开始录制，音量 `−` 停止；显示 REC 时间和每只手的状态。
- 保存原始 RGB、头部、双手关节及所有可用 Tracker 位姿。
- 左右手分别根据腕部位置匹配 Tracker，设备 ID 不预设左右。
- 同一个 Tracker 不能同时绑定两只手；歧义时等待双手分开。
- 每只手分别标定位置与旋转偏移，锁定后使用 Tracker 定手腕、PICO 定相对手指形状。
- 一侧失效时单独回退；不带 Tracker 时保留原始 PICO 输出。
- 保留已有的相机恢复、前台防休眠和相册视频发布代码。

## 目录

```text
unity-overlay/          自有 Unity/Android 源码及配置，覆盖到另备的基础工程
  Assets/EgoCapture/    录制、双手配对、融合、MP4 包装器、场景
  Assets/Editor/        构建入口、Manifest 配置、供后续使用的自测
  Assets/Plugins/Android/  音量键、相册发布、相机恢复 Activity
  Assets/XR/           XR 配置
  Assets/Resources/    PICO 手追与透视设置
  Packages/            依赖清单，不包含 SDK 本体
  ProjectSettings/     Unity 配置
tools/                  安装源码覆盖层、稳定性分析、视频拼接
docs/                   安装、融合逻辑、数据格式、第三方来源
```

这份仓库是**源码覆盖层**，需要另备基础 Unity 工程和第三方依赖。没有上传 PICO SDK、NatCorder 插件、APK、Unity 缓存或真实录制数据。

## 使用

1. 按 [安装与构建](docs/setup.md) 准备 Unity 2022.3.21f1、PICO SDK 3.4.0 和基础工程。
2. 将本仓库覆盖层复制到基础工程，再由 Unity 构建。
3. 设备上连接两个 Tracker，使用独立追踪模式；分别固定在左、右手腕附近。
4. 自己按音量 `+` 开始录制。让需要配对的手和 Tracker 出现在头显追踪视野里，双腕保持分开。
5. 查看 `LEFT Tracker <ID>`、`RIGHT Tracker <ID>`。每只手独立从 `PAIR` → `CAL` → `FUSED LOCKED`。
6. 自己按音量 `−` 停止。原始数据与融合数据同时保存。

配对与标定从每次录制开始，不是在 READY 状态完成。只看到一只手时可以先处理该侧，另一侧之后进入视野再处理。配对锁定后，双手交叉或暂时离开视野不会自动交换 ID；重新佩戴或换 Tracker 应停止并重新录制。

详见 [双手配对与融合](docs/fusion.md) 和 [数据格式](docs/data-format.md)。

## 分析和视频

```bash
python3 -m pip install -r requirements.txt
python3 tools/analyze_stability.py /path/to/session --start 2 --end 23
python3 tools/render_two_recordings.py /path/to/tracker_session /path/to/pico_session /path/to/comparison.mp4 --hand right --start 2 --end 23
```

视频上半部为佩戴 Tracker 的原始 RGB 与融合关节，下半部为不佩戴 Tracker 的原始 RGB 与 PICO 关节。`--hand left` 可使用左手数据，但要求对应录制包含左侧融合状态文件。非 macOS 平台需传入 `--font /path/to/chinese-font.ttf`。

选择真正静止的时间窗；两次录制的头部动作未同步，不能当作严格配对实验。静止波动衡量稳定性，不等于相对真实位置的绝对误差，也不代表手指精度。固定腕带偏移仍可能包含系统误差。

## 版本与发布

当前版本为 V1.0，支持左右手独立融合、一对一 Tracker 绑定、双侧状态日志和双手 HUD。

上传时推送这个仓库目录；不要推送整个本地工作目录。第三方来源和许可证说明见 [第三方说明](docs/third-party.md)。
