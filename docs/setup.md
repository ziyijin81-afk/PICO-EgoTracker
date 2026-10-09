# 安装与构建

## 本地版本

- Unity Editor：2022.3.21f1，安装 Android Build Support、SDK/NDK、OpenJDK。
- PICO Unity Integration SDK：3.4.0，含对应的企业 RGB Camera / Enterprise 支持。
- Unity XR Hands：1.3.0；其余版本见 `unity-overlay/Packages/manifest.json`。
- MP4 后端：基础工程中的 NatCorder，代码依赖 `NatSuite.Recorders`，包括 Android 原生插件。
- 开发设备：PICO 4 Ultra Enterprise；双手融合额外需要两个能输出独立位姿的 Motion Tracker。

仓库不附带第三方插件。需要企业相机访问能力和对应权限，不保证消费版头显同样可用。

## 准备基础工程

当前工程基于 PICO Motion Tracker Sample，记录的基础提交为 `4a74a91c4fef6794c8d0d6577c5db3c463bee09e`。第三方样例来源见 [说明](third-party.md)。

```bash
git lfs install
git clone https://github.com/Pico-Developer/PICOMotionTrackerSample-Unity.git PICOEgoTrackerBase
git -C PICOEgoTrackerBase checkout 4a74a91c4fef6794c8d0d6577c5db3c463bee09e
git -C PICOEgoTrackerBase lfs pull
```

1. 另从 PICO 官方渠道获取 Integration SDK 3.4.0，解压为基础工程的 `Packages/PICO-Unity-Integration-SDK-3.4.0/`。必须包含 `package.json`，不要额外套一层目录。
2. 保留/合法安装基础工程中的 NatCorder 及原生 Android 编码依赖。若缺失，获取提供 `NatSuite.Recorders.Internal.NativeRecorder`、`NatCorder.CreateMP4Recorder` 的对应版本。
3. 建议在这个单独准备的基础工程中应用覆盖层。覆盖会替换相应源码、依赖清单和 ProjectSettings；不要对有其他用途的工程直接覆盖。

```bash
python3 tools/apply_overlay.py /path/to/PICOEgoTrackerBase
```

基础样例中与 Avatar 有关的旧演示场景、脚本、资源可能引用已移除的 Avatar 包。本地工作工程已清理这些演示。新建基础工程如有这类编译依赖，应移除不用于 EgoCapture 的旧演示或按原样例补齐对应依赖；本次覆盖层的全新安装尚未验证。

## 构建

在 Unity Hub 中打开准备后的基础工程，Android XR loader 使用 PICO；保留 `PXR_ProjectSetting` 的 handTracking 与 videoSeeThrough。构建入口会生成空透视采集场景并配置 Android ARM64、IL2CPP 和应用 ID `org.local.picoegotracker`。

macOS 命令示例：

```bash
"/Applications/Unity/Hub/Editor/2022.3.21f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -nographics -quit \
  -projectPath /path/to/PICOEgoTrackerBase \
  -executeMethod EgoCaptureBuild.BuildAndroid \
  -logFile /path/to/build.log
```

APK 输出到基础工程同级的 `Builds/PICOEgoTrackerFusion.apk`。构建入口保留代码自测，后续执行构建时会运行；本次没有完成构建或报告自测通过。

安装需自行连接并授权 ADB：

```bash
adb install -r /path/to/Builds/PICOEgoTrackerFusion.apk
```

构建、安装不会主动开始录制。用户打开应用并按音量 `+` 才开始。

## GitHub 上传

上传的是本源码仓库，而不是基础工程和 SDK。可先在 GitHub 新建私有空仓库，然后在本目录执行：

```bash
git add .
git commit -m "Add draft bilateral PICO hand and Tracker capture"
git remote add origin <your-repository-url>
git push -u origin main
```

推送需要 GitHub 对目标仓库的写入授权。源码版本为 2.2.0-draft，双手版尚未完成构建和实机验证。
