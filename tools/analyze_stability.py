"""Static world-coordinate wrist stability, not absolute tracking accuracy."""
import argparse
import json
from pathlib import Path
import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("session", type=Path)
parser.add_argument("--start", type=float, required=True)
parser.add_argument("--end", type=float, required=True)
parser.add_argument("--output", type=Path)
args = parser.parse_args()
if args.start < 0 or args.end <= args.start:
    parser.error("require 0 <= start < end; choose the interval from the original video")
base = args.session.resolve()
raw = np.loadtxt(base / "poses.txt", ndmin=2)
fused = np.loadtxt(base / "poses_fused.txt", ndmin=2)
tracking = np.loadtxt(base / "tracking_status.txt", ndmin=2)
if raw.shape != fused.shape or raw.shape[1] != 373:
    raise ValueError("expected matched head + left 26 + right 26 joint streams (373 columns)")
if not np.array_equal(raw[:, :2], fused[:, :2]) or not np.array_equal(raw[:, :2], tracking[:, :2]):
    raise ValueError("frame indices and timestamps must match")
t = raw[:, 1] / 1000
window = (t >= args.start) & (t < args.end)

def stats(values):
    if not len(values):
        return None
    position = values[:, :3]
    reference = np.median(position, axis=0)
    distance = np.linalg.norm(position - reference, axis=1) * 100
    q = values[:, 3:7]
    norm = np.linalg.norm(q, axis=1)
    if np.any(norm < 1e-9) or not np.all(np.isfinite(values)):
        raise ValueError("invalid pose in selected samples")
    q = q / norm[:, None]
    _, vectors = np.linalg.eigh(q.T @ q)
    angle = np.degrees(2 * np.arccos(np.clip(abs(q @ vectors[:, -1]), 0, 1)))
    return dict(frames=len(values), position_median_cm=float(np.median(distance)),
                position_p95_cm=float(np.percentile(distance, 95)),
                position_max_cm=float(distance.max()), angle_p95_deg=float(np.percentile(angle, 95)),
                reference_position_m=reference.tolist(), reference_quaternion_xyzw=vectors[:, -1].tolist())

report = dict(session=base.name, window_s=[args.start, args.end],
              metric="World-coordinate wrist stability relative to each stream's median position and Markley mean orientation; no ground truth",
              hands={})
for side, offset, column, name in [("left", 16, 3, "fusion_status_left.txt"),
                                   ("right", 198, 6, "fusion_status.txt")]:
    wrist = raw[:, offset:offset+7]
    # Missing XR joints are encoded as zero position + identity quaternion.
    # This cannot distinguish a genuine wrist exactly at the tracking origin.
    missing = np.all(wrist[:, :3] == 0, axis=1) & np.all(wrist[:, 3:] == [0, 0, 0, 1], axis=1)
    valid = (tracking[:, column] == 1) & ~missing
    mask = window & valid
    hand = dict(raw_all_valid=stats(wrist[mask]), missing_raw_frames=int(np.sum(window & ~valid)),
                raw_fused_exactly_equal=bool(np.array_equal(wrist, fused[:, offset:offset+7])))
    path = base / name
    if path.exists():
        rows = [line.split() for line in path.read_text().splitlines() if line.strip() and not line.startswith("#")]
        if len(rows) != len(raw) or any(int(r[0]) != int(raw[i, 0]) or int(r[1]) != int(raw[i, 1]) for i, r in enumerate(rows)):
            raise ValueError(f"{name} does not align with poses")
        paired = mask & np.array([r[2] == "tracker_root_pico_fingers" for r in rows])
        hand["raw_on_fused_frames"] = stats(wrist[paired])
        hand["fusion_on_same_frames"] = stats(fused[paired, offset:offset+7])
        hand["tracker_ids_in_window"] = sorted({int(rows[i][3]) for i in np.flatnonzero(window) if int(rows[i][3]) != 0})
        sources = [rows[i][2] for i in np.flatnonzero(window)]
        hand["sources_in_window"] = {s: sources.count(s) for s in sorted(set(sources))}
    else:
        hand["fusion_note"] = "No per-hand status file; left fusion cannot be inferred for old right-only recordings"
    report["hands"][side] = hand
output = args.output or base / "stability_report.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(report, ensure_ascii=False, indent=2))
print(json.dumps(report, ensure_ascii=False, indent=2))
