using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public static class EgoFusionSelfTest
{
    public static void Run()
    {
        var fusion = new EgoWristFusion();
        Vector3 mountOffset = new Vector3(0.01f, 0.03f, -0.02f);
        Quaternion relativeRotation = Quaternion.Euler(5f, 0f, 0f);
        EgoWristFusion.Result result = default;
        Pose previousTracker = default;
        for (int i = 0; i < 140; i++)
        {
            Quaternion orientation = Quaternion.Euler(0f, i * 0.3f, 0f);
            Pose tracker = new Pose(new Vector3(0.1f + i * 0.0003f, 0.9f, 0.2f), orientation);
            previousTracker = tracker;
            Pose wrist = new Pose(tracker.position + orientation * mountOffset +
                new Vector3((i % 5 - 2) * 0.0005f, 0, 0), orientation * relativeRotation);
            var joints = JointsFromWrist(wrist);
            var valid = AllValid();
            result = fusion.Process(i / 90.0, true, tracker, true, joints, valid);
            if (i == 100)
            {
                Require(result.source == "tracker_root_pico_fingers", "fusion did not start");
                Require(Vector3.Distance(result.joints[1].position,
                    tracker.position + orientation * mountOffset) < 0.004f,
                    "fused wrist position did not use tracker root");
                Require(Mathf.Abs(Vector3.Distance(result.joints[1].position,
                    result.joints[10].position) - Vector3.Distance(joints[1].position,
                    joints[10].position)) < 0.00001f, "finger geometry changed");
            }
        }

        // Hold one reported tracker position while the optical hand moves.
        // The app must not mistake the held position for a stable measurement.
        bool fellBack = false;
        for (int i = 140; i < 210; i++)
        {
            Pose opticalWrist = new Pose(previousTracker.position +
                new Vector3((i - 140) * 0.0005f, 0, 0), Quaternion.identity);
            result = fusion.Process(i / 90.0, true, previousTracker, true,
                                    JointsFromWrist(opticalWrist), AllValid());
            if (result.source == "pico_tracker_stale") fellBack = true;
        }
        Require(fellBack, "held tracker position did not trigger PICO fallback");

        // After tracker reacquisition and hand occlusion, emit only a wrist.
        bool wristOnly = false;
        for (int i = 210; i < 290; i++)
        {
            Pose tracker = new Pose(new Vector3(0.2f + (i - 210) * 0.0003f,
                0.9f, 0.2f), Quaternion.identity);
            result = fusion.Process(i / 90.0, true, tracker, false,
                                    EmptyJoints(), new bool[26]);
            if (result.source == "tracker_wrist_only") wristOnly = true;
        }
        Require(wristOnly, "hand dropout did not preserve tracker wrist");
        Require(result.joints[2].position == Vector3.zero,
                "unobserved finger should remain a placeholder");
        CheckFixedMount();
        CheckTrackerPairing();
        CheckBilateralPairing();
        CheckBilateralFusion();
        Debug.Log("EGO_FUSION_SELF_TEST_PASS");
    }

    private static void CheckBilateralPairing()
    {
        var pairing = new EgoBilateralTrackerPairing();
        var trackers = new Dictionary<long, Pose>();
        Vector3 left = new Vector3(-0.3f, 1, 0.2f), right = new Vector3(0.3f, 1, 0.2f);
        for (int i = 0; i < 45; i++)
        {
            // Deliberately reversed numeric IDs and enumeration order.
            trackers[2] = new Pose(left + Vector3.up * (0.05f + i * 0.00001f), Quaternion.identity);
            trackers[1] = new Pose(right + Vector3.up * (0.06f + i * 0.00001f), Quaternion.identity);
            pairing.Process(i / 90.0, true, left, true, right, trackers);
        }
        Require(pairing.Left.SelectedId == 2 && pairing.Right.SelectedId == 1,
                "bilateral pairing assumed numeric ID anatomy");
        trackers.Clear();
        pairing.Process(1, false, left, false, right, trackers);
        Require(pairing.Left.SelectedId == 2 && pairing.Right.SelectedId == 1,
                "dropout lost locked anatomical assignment");
        trackers[2] = new Pose(right, Quaternion.identity);
        trackers[1] = new Pose(left, Quaternion.identity);
        pairing.Process(2, true, right, true, left, trackers);
        Require(pairing.Left.SelectedId == 2 && pairing.Right.SelectedId == 1,
                "crossing hands swapped device IDs");

        pairing.Reset();
        Require(pairing.Left.SelectedId == 0 && pairing.Right.SelectedId == 0,
                "new recording retained bilateral assignment");
        // One tracker equally close to both hands must not go to the first caller.
        trackers.Clear();
        for (int i = 0; i < 60; i++)
        {
            trackers[7] = new Pose(new Vector3(i * 0.00001f, 1.05f, 0.2f), Quaternion.identity);
            pairing.Process(i / 90.0, true, new Vector3(-0.1f, 1, 0.2f),
                            true, new Vector3(0.1f, 1, 0.2f), trackers);
            Require(pairing.Left.SelectedId == 0 && pairing.Right.SelectedId == 0,
                    "ambiguous shared tracker was claimed by update order");
        }

        pairing.Reset();
        for (int i = 0; i < 60; i++)
        {
            trackers[7] = new Pose(left + Vector3.up * (0.05f + i * 0.00001f), Quaternion.identity);
            pairing.Process(i / 90.0, true, left, true, right, trackers);
        }
        Require(pairing.Left.SelectedId == 7 && pairing.Right.SelectedId == 0,
                "single tracker was assigned to both hands");
        // The already-bound tracker stays reserved even while its hand is hidden.
        for (int i = 60; i < 120; i++)
        {
            trackers[7] = new Pose(right + Vector3.up * (0.05f + i * 0.00001f), Quaternion.identity);
            pairing.Process(i / 90.0, false, left, true, right, trackers);
            Require(pairing.Right.SelectedId == 0, "hidden hand's tracker was stolen");
        }

        pairing.Reset();
        trackers.Clear();
        for (int i = 0; i < 100; i++)
        {
            trackers[2] = new Pose(left + Vector3.up * (0.05f + i * 0.00001f), Quaternion.identity);
            trackers[1] = new Pose(right + Vector3.up * (0.06f + i * 0.00001f), Quaternion.identity);
            pairing.Process(i / 90.0, i >= 50, left, true, right, trackers);
            if (i < 50) Require(pairing.Left.SelectedId == 0, "paired unseen left wrist");
            Require(pairing.Left.SelectedId == 0 || pairing.Left.SelectedId != pairing.Right.SelectedId,
                    "duplicate tracker assignment");
        }
        Require(pairing.Left.SelectedId == 2 && pairing.Right.SelectedId == 1,
                "asynchronous hand visibility prevented independent pairing");
        Debug.Log("EGO_BILATERAL_PAIRING_SELF_TEST_PASS");
    }

    private static void CheckBilateralFusion()
    {
        var leftFusion = new EgoWristFusion();
        var rightFusion = new EgoWristFusion();
        Vector3 leftOffset = new Vector3(0.01f, 0.03f, -0.02f);
        Vector3 rightOffset = new Vector3(-0.02f, 0.04f, 0.01f);
        Quaternion leftRotation = Quaternion.Euler(12, -8, 4);
        Quaternion rightRotation = Quaternion.Euler(-7, 15, -3);
        Pose lastLeftTracker = default;
        for (int i = 0; i < 200; i++)
        {
            Pose lt = new Pose(new Vector3(-0.3f + i * 0.0003f, 1, 0.2f), Quaternion.Euler(0, i * 0.2f, 0));
            Pose rt = new Pose(new Vector3(0.3f + i * 0.0003f, 1, 0.2f), Quaternion.Euler(i * 0.1f, 0, 0));
            lastLeftTracker = lt;
            Pose lw = new Pose(lt.position + lt.rotation * leftOffset, lt.rotation * leftRotation);
            Pose rw = new Pose(rt.position + rt.rotation * rightOffset, rt.rotation * rightRotation);
            Pose[] lj = JointsFromWrist(lw), rj = JointsFromWrist(rw);
            var l = leftFusion.Process(i / 90.0, true, lt, true, lj, AllValid());
            var r = rightFusion.Process(i / 90.0, i >= 80, rt, true, rj, AllValid());
            if (i == 100) Require(l.calibrationFrozen && !r.calibrationFrozen,
                    "left calibration leaked into right calibration");
            if (i >= 175)
            {
                Require(l.source == "tracker_root_pico_fingers" && r.source == "tracker_root_pico_fingers",
                        "both hands failed to fuse");
                Require(Vector3.Distance(l.trackerLocalWristOffset, leftOffset) < 0.00001f &&
                        Vector3.Distance(r.trackerLocalWristOffset, rightOffset) < 0.00001f,
                        "bilateral mount offsets were shared");
                Require(Quaternion.Angle(l.trackerToWristRotation, leftRotation) < 0.1f &&
                        Quaternion.Angle(r.trackerToWristRotation, rightRotation) < 0.1f,
                        "bilateral mount rotations were shared");
                Require(Vector3.Distance(l.joints[1].position, lw.position) < 0.00001f &&
                        Vector3.Distance(r.joints[1].position, rw.position) < 0.00001f,
                        "a hand used the opposite tracker root");
            }
            Require(lj[1].position == lw.position && rj[1].position == rw.position,
                    "fusion mutated original PICO poses");
        }
        bool leftFallback = false;
        for (int i = 200; i < 265; i++)
        {
            Pose lw = new Pose(lastLeftTracker.position + Vector3.right * ((i - 200) * 0.001f), Quaternion.identity);
            Pose rt = new Pose(new Vector3(0.3f + i * 0.0003f, 1, 0.2f), Quaternion.identity);
            var l = leftFusion.Process(i / 90.0, true, lastLeftTracker, true, JointsFromWrist(lw), AllValid());
            var r = rightFusion.Process(i / 90.0, true, rt, true, JointsFromWrist(rt), AllValid());
            if (l.source == "pico_tracker_stale") leftFallback = true;
            Require(r.source == "tracker_root_pico_fingers", "left stale tracker disabled right fusion");
        }
        Require(leftFallback, "left stale tracker failed to fall back independently");
        foreach (var fusion in new[] { leftFusion, rightFusion })
        {
            fusion.Reset();
            var joints = JointsFromWrist(new Pose(new Vector3(0.1f, 1, 0.2f), Quaternion.identity));
            var r = fusion.Process(0, false, default, true, joints, AllValid());
            Require(r.source == "pico_only" && !r.calibrationFrozen, "no tracker started fusion");
            for (int j = 0; j < 26; j++)
                Require(r.joints[j].position == joints[j].position && r.joints[j].rotation == joints[j].rotation,
                        "no tracker changed optical joints");
        }
        Debug.Log("EGO_BILATERAL_FUSION_SELF_TEST_PASS");
    }

    private static void CheckTrackerPairing()
    {
        Vector3 wrist = new Vector3(0.4f, 1, 0.3f);
        var pairing = new EgoWristTrackerPairing();
        var trackers = new Dictionary<long, Pose>();
        // Reversed insertion order must not make ID 2 the right-hand tracker.
        for (int i = 0; i < 40; i++)
        {
            trackers[2] = new Pose(wrist + Vector3.right * 0.86f, Quaternion.identity);
            trackers[1] = new Pose(wrist + Vector3.up * 0.06f + Vector3.right * (i * 0.00001f), Quaternion.identity);
            long selected = pairing.Process(i / 90.0, true, wrist, trackers);
            if (i < 29) Require(selected == 0, "paired from too few samples");
        }
        Require(pairing.SelectedId == 1, "numeric ID preference selected the remote tracker");
        // After lock, missing or closer competing devices must not silently swap wrists.
        trackers.Clear();
        trackers[2] = new Pose(wrist, Quaternion.identity);
        Require(pairing.Process(1, true, wrist, trackers) == 1, "paired device changed mid-recording");
        pairing.Reset();
        Require(pairing.SelectedId == 0, "new recording retained old pairing");
        for (int i = 0; i < 50; i++)
        {
            trackers[1] = new Pose(wrist + Vector3.up * (0.06f + i * 0.00001f), Quaternion.identity);
            trackers[2] = new Pose(wrist + Vector3.up * (0.07f + i * 0.00001f), Quaternion.identity);
            Require(pairing.Process(i / 90.0, true, wrist, trackers) == 0, "ambiguous devices were paired");
        }
        pairing.Reset();
        trackers.Clear();
        trackers[2] = new Pose(wrist + Vector3.up * 0.50f, Quaternion.identity);
        for (int i = 0; i < 50; i++)
            Require(pairing.Process(i / 90.0, true, wrist, trackers) == 0, "single remote tracker was paired");
        pairing.Reset();
        trackers[2] = new Pose(wrist + Vector3.up * 0.05f, Quaternion.identity);
        for (int i = 0; i < 60; i++)
            Require(pairing.Process(i / 90.0, false, wrist, trackers) == 0, "paired without optical wrist");
        // Its position has been held long enough to be rejected when the hand returns.
        for (int i = 60; i < 110; i++)
            Require(pairing.Process(i / 90.0, true, wrist, trackers) == 0, "paired a long-held API pose");
        pairing.Reset();
        for (int i = 0; i < 50; i++)
        {
            trackers[2] = new Pose(wrist + Vector3.up * (0.05f + i * 0.00001f), Quaternion.identity);
            pairing.Process(i / 90.0, true, wrist, trackers);
        }
        Require(pairing.SelectedId == 2, "valid single nearby tracker was not paired");
        Debug.Log("EGO_TRACKER_PAIRING_SELF_TEST_PASS");
    }

    private static void CheckFixedMount()
    {
        var fusion = new EgoWristFusion();
        Vector3 offset = new Vector3(0.02f, -0.03f, 0.04f);
        Quaternion relative = Quaternion.Euler(10, -20, 5);
        for (int i = 0; i < 300; i++)
        {
            Pose tracker = new Pose(new Vector3(i * 0.0003f, 0.9f, 0.2f),
                                    Quaternion.Euler(0, i * 0.2f, 0));
            Pose wrist = new Pose(tracker.position + tracker.rotation * offset,
                                  tracker.rotation * relative);
            if (i >= EgoWristFusion.MinimumCalibrationSamples)
            {
                // A large optical root error must not change the calibrated
                // tracker-to-wrist transform. Finger articulation still passes.
                wrist.position += new Vector3(0.05f, -0.03f, 0.02f);
                wrist.rotation *= Quaternion.Euler(35f, 20f, 0f);
            }
            else if ((i & 1) != 0)
                wrist.rotation = new Quaternion(-wrist.rotation.x, -wrist.rotation.y,
                                                 -wrist.rotation.z, -wrist.rotation.w);
            Pose[] joints = JointsFromWrist(wrist);
            if (i >= EgoWristFusion.MinimumCalibrationSamples)
                joints[10].position = wrist.position + wrist.rotation * new Vector3(0.04f, 0.03f, 0.01f);
            var result = fusion.Process(i / 90.0, true, tracker, true, joints, AllValid());
            if (i < EgoWristFusion.MinimumCalibrationSamples - 1)
                Require(!result.calibrationFrozen, "mount locked before enough paired samples");
            else
            {
                Require(result.calibrationFrozen, "mount failed to lock");
                Require(result.calibrationSamples == EgoWristFusion.MinimumCalibrationSamples,
                        "calibration kept collecting after lock");
                Require(Vector3.Distance(result.trackerLocalWristOffset, offset) < 0.00001f,
                        "optical noise changed the locked position offset");
                Require(Quaternion.Angle(result.trackerToWristRotation, relative) < 0.1f,
                        "rotation averaging or lock failed");
                Require(Quaternion.Angle(result.joints[1].rotation, tracker.rotation * relative) < 0.1f,
                        "optical root rotation leaked into fused wrist");
                Require(Vector3.Distance(result.joints[1].position,
                    tracker.position + tracker.rotation * offset) < 0.00001f,
                    "optical root position leaked into fused wrist");
                Vector3 expectedLocal = Quaternion.Inverse(wrist.rotation) * (joints[10].position - wrist.position);
                Vector3 actualLocal = Quaternion.Inverse(result.joints[1].rotation) *
                    (result.joints[10].position - result.joints[1].position);
                Require(Vector3.Distance(expectedLocal, actualLocal) < 0.00001f,
                        "finger articulation was lost");
            }
        }
        fusion.Reset();
        Pose newTracker = new Pose(new Vector3(0.1f, 1, 0.2f), Quaternion.identity);
        var reset = fusion.Process(0, true, newTracker, true,
            JointsFromWrist(newTracker), AllValid());
        Require(!reset.calibrationFrozen && reset.calibrationSamples == 1,
                "a new recording reused an old mount calibration");
    }

    public static void ReplayFromCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        int argIndex = Array.IndexOf(args, "-egoReplayDir");
        if (argIndex < 0) return;
        string directory = args[argIndex + 1];
        string output = Path.Combine(directory, "locked_mount_replay");
        Directory.CreateDirectory(output);
        var c = CultureInfo.InvariantCulture;
        var trackerRows = new Dictionary<int, Dictionary<long, Pose>>();
        foreach (string line in File.ReadLines(Path.Combine(directory, "tracker.txt")))
        {
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
            string[] row = line.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
            int frame = int.Parse(row[0], c);
            if (!trackerRows.TryGetValue(frame, out Dictionary<long, Pose> frameTrackers))
            {
                frameTrackers = new Dictionary<long, Pose>();
                trackerRows[frame] = frameTrackers;
            }
            if (row[3] == "0" && row[4] == "1")
                frameTrackers[long.Parse(row[2], c)] = ParsePose(row, 5);
        }
        var trackedRows = new Dictionary<int, bool>();
        foreach (string line in File.ReadLines(Path.Combine(directory, "tracking_status.txt")))
        {
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
            string[] row = line.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
            trackedRows[int.Parse(row[0], c)] = row[6] == "1";
        }
        var fusion = new EgoWristFusion();
        var pairing = new EgoWristTrackerPairing();
        int frameCount = 0, fusedCount = 0;
        using (var poses = new StreamWriter(Path.Combine(output, "poses_fused.txt")))
        using (var status = new StreamWriter(Path.Combine(output, "fusion_status.txt")))
        {
            poses.WriteLine("# Offline replay through wrist proximity pairing and EgoWristFusion 2.1.11; original recording unchanged");
            status.WriteLine("# idx t_ms source calibration_samples calibration_frozen offset_xyz tracker_to_wrist_xyzw selected_tracker_id");
            foreach (string line in File.ReadLines(Path.Combine(directory, "poses.txt")))
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
                string[] row = line.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
                int idx = int.Parse(row[0], c);
                double seconds = double.Parse(row[1], c) / 1000;
                bool hand = trackedRows[idx];
                var joints = new Pose[26];
                var valid = new bool[26];
                int rightStart = 9 + 26 * 7;
                for (int i = 0; i < 26; i++)
                {
                    joints[i] = ParsePose(row, rightStart + i * 7);
                    valid[i] = hand && joints[i].position != Vector3.zero;
                }
                if (!trackerRows.TryGetValue(idx, out Dictionary<long, Pose> frameTrackers))
                    frameTrackers = new Dictionary<long, Pose>();
                long selected = pairing.Process(seconds, hand && valid[1], joints[1].position, frameTrackers);
                Pose tracker = new Pose(Vector3.zero, Quaternion.identity);
                bool trackerValid = selected != 0 && frameTrackers.TryGetValue(selected, out tracker);
                var result = fusion.Process(seconds, trackerValid, tracker, hand, joints, valid);
                var merged = new StringBuilder();
                for (int i = 0; i < rightStart; i++)
                {
                    if (i > 0) merged.Append(' ');
                    merged.Append(row[i]);
                }
                foreach (Pose joint in result.joints) AppendPose(merged, joint, c);
                poses.WriteLine(merged);
                status.WriteLine(row[0] + " " + row[1] + " " + result.source + " " +
                    result.calibrationSamples + " " + (result.calibrationFrozen ? 1 : 0) + " " +
                    result.trackerLocalWristOffset.x.ToString("R", c) + " " +
                    result.trackerLocalWristOffset.y.ToString("R", c) + " " +
                    result.trackerLocalWristOffset.z.ToString("R", c) + " " +
                    result.trackerToWristRotation.x.ToString("R", c) + " " +
                    result.trackerToWristRotation.y.ToString("R", c) + " " +
                    result.trackerToWristRotation.z.ToString("R", c) + " " +
                    result.trackerToWristRotation.w.ToString("R", c) + " " + selected);
                frameCount++;
                if (result.source == "tracker_root_pico_fingers") fusedCount++;
            }
        }
        Debug.Log("EGO_FIXED_MOUNT_REPLAY_PASS frames=" + frameCount + " fused=" + fusedCount + " output=" + output);
    }

    private static Pose ParsePose(string[] row, int start)
    {
        var c = CultureInfo.InvariantCulture;
        return new Pose(new Vector3(float.Parse(row[start], c), float.Parse(row[start+1], c), float.Parse(row[start+2], c)),
            new Quaternion(float.Parse(row[start+3], c), float.Parse(row[start+4], c),
                           float.Parse(row[start+5], c), float.Parse(row[start+6], c)));
    }

    private static void AppendPose(StringBuilder row, Pose pose, CultureInfo c)
    {
        foreach (float value in new[] {pose.position.x, pose.position.y, pose.position.z,
                pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w})
            row.Append(' ').Append(value.ToString("R", c));
    }

    private static Pose[] JointsFromWrist(Pose wrist)
    {
        var joints = new Pose[26];
        for (int i = 0; i < joints.Length; i++)
            joints[i] = new Pose(wrist.position + wrist.rotation *
                new Vector3(i * 0.001f, 0.01f, 0), wrist.rotation);
        joints[1] = wrist;
        return joints;
    }

    private static Pose[] EmptyJoints()
    {
        var joints = new Pose[26];
        for (int i = 0; i < joints.Length; i++)
            joints[i] = new Pose(Vector3.zero, Quaternion.identity);
        return joints;
    }

    private static bool[] AllValid()
    {
        var valid = new bool[26];
        for (int i = 0; i < valid.Length; i++) valid[i] = true;
        return valid;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("EgoFusionSelfTest: " + message);
    }
}
