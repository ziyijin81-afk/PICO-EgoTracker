# 录制数据格式

每次录制在 `Application.persistentDataPath/Recordings/yyyyMMdd_HHmmss/` 保存：

| 文件 | 内容 |
|---|---|
| `internal.mp4` | 原始左 RGB 相机视频，无关节覆盖 |
| `poses.txt` | 原始头部、左手 26 关节、右手 26 关节 |
| `poses_fused.txt` | 同样布局，左右分别替换为各自融合/回退结果 |
| `tracker.txt` | 每帧各 Tracker 的原始位置、旋转和 API 状态 |
| `fusion_status_left.txt` | 左手 ID、配对后的融合状态和标定偏移 |
| `fusion_status.txt` | 右手状态，名称保留以兼容旧版分析 |
| `tracking_status.txt` | 双手手追状态、有效关节数和 Tracker 数 |
| `video_frames.txt` | 视频编码时间戳与位姿读取时钟映射 |
| `log.txt` | 版本、录制状态、左右配对事件、采集计数 |

两个融合状态文件每帧各一行，与位姿的 `pose_idx`、`t_ms` 对齐。行开头 `#` 是表头，解析时忽略。

## poses

每行 373 列：`idx t_ms`，再加头部 7 列，左手 182 列，右手 182 列。每个位姿是 `x y z qx qy qz qw`，位置单位为米，时间单位为毫秒。

采用 OpenXR 关节顺序：0 Palm、1 Wrist、2–5 Thumb、6–10 Index、11–15 Middle、16–20 Ring、21–25 Little。这里指记录文件的排列，读取来源仍为 XR Hands 的 PICO 子系统。

以 Python 0-based 列索引表示：

```text
head = row[2:9]
left = row[9:191].reshape(26, 7)
right = row[191:373].reshape(26, 7)
left wrist = row[16:23]
right wrist = row[198:205]
```

缺失关节为 `0 0 0 0 0 0 1`。必须结合状态文件判断，不要将占位零作为真实坐标。当前状态文件记录有效数量，不包含原始每关节的独立有效位；真腕部恰好位于原点时，单靠占位数值无法区分。

## fusion status

两侧文件各 18 列：

```text
pose_idx t_ms source tracker_id tracker_api_valid tracker_position_fresh
pico_<side>_tracked valid_<side>_joints calibration_samples hand_orientation_observed
offset_x_m offset_y_m offset_z_m calibration_frozen
tracker_to_wrist_qx tracker_to_wrist_qy tracker_to_wrist_qz tracker_to_wrist_qw
```

`tracker_id=0` 表示尚未选定。设备 ID 由本次连接提供，可能跨次变化。`source`：

- `tracker_root_pico_fingers`：Tracker 定手腕，PICO 定手指。
- `tracker_wrist_only`：仅腕部有效，手指是缺失占位。
- `pico_calibrating`：正在标定，暂输出原始 PICO。
- `pico_only`：没有可用的已选定 Tracker，输出原始 PICO。
- `pico_tracker_stale`：选定 Tracker 的 API 有效，但新鲜度检查未通过，回退 PICO。
- `no_hand_pose` / `no_hand_tracker_stale`：当前没有可用的整手姿态。

`valid_<side>_joints` 统计原始 PICO 有效关节数量，不能将它当作融合输出的有效关节数。`tracker_wrist_only` 必须单独处理。

视频时间通过各自 `video_frames.txt` 映射到位姿时钟；映射使用应用读取/编码时间，未标定相机曝光与追踪算法延迟。视频和位姿不要求帧率相同。
