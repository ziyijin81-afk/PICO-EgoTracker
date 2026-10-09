using System;
using System.Collections.Generic;
using UnityEngine;

// Experimental, session-local fusion. Raw PICO and Tracker streams remain
// separate; this class only produces an additional hand pose stream.
public sealed class EgoWristFusion
{
    public const int JointCount = 26;
    public const int WristIndex = 1;
    public const int MinimumCalibrationSamples = 90;
    private const float SamePositionEpsilonMeters = 0.000001f;
    private const float HandMovedWhileHeldMeters = 0.02f;

    public struct Result
    {
        public Pose[] joints;
        public string source;
        public bool trackerApiValid;
        public bool trackerPositionFresh;
        public bool handTracked;
        public bool handOrientationObserved;
        public int calibrationSamples;
        public int validJointCount;
        public Vector3 trackerLocalWristOffset;
        public Quaternion trackerToWristRotation;
        public bool calibrationFrozen;
    }

    private readonly List<Vector3> offsetSamples = new List<Vector3>(MinimumCalibrationSamples);
    private readonly List<Quaternion> rotationSamples = new List<Quaternion>(MinimumCalibrationSamples);
    private bool calibrated;
    private Vector3 offset;
    private Quaternion trackerToHandRotation = Quaternion.identity;
    private bool relativeRotationReady;
    private bool havePreviousTracker;
    private Vector3 previousTrackerPosition;
    private double previousTrackerTime;
    private double positionHeldSince = -1;
    private Vector3 handAtHeldStart;
    private bool handAtHeldStartValid;
    private double quarantineUntil;

    public void Reset()
    {
        offsetSamples.Clear();
        rotationSamples.Clear();
        calibrated = false;
        offset = Vector3.zero;
        trackerToHandRotation = Quaternion.identity;
        relativeRotationReady = false;
        havePreviousTracker = false;
        positionHeldSince = -1;
        handAtHeldStartValid = false;
        quarantineUntil = 0;
    }

    private static Pose MissingPose() => new Pose(Vector3.zero, Quaternion.identity);

    private static float MedianComponent(List<Vector3> samples, int axis)
    {
        var values = new float[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            values[i] = axis == 0 ? samples[i].x : axis == 1 ? samples[i].y : samples[i].z;
        Array.Sort(values);
        int middle = values.Length / 2;
        return (values.Length & 1) == 1 ? values[middle] :
            0.5f * (values[middle - 1] + values[middle]);
    }

    private static Vector3 MedianOffset(List<Vector3> samples) =>
        new Vector3(MedianComponent(samples, 0), MedianComponent(samples, 1),
                    MedianComponent(samples, 2));

    private static Quaternion AverageRotation(List<Quaternion> samples)
    {
        // q and -q describe the same rotation. Align signs before averaging so
        // an SDK quaternion sign change cannot cancel the calibration.
        Quaternion reference = samples[0];
        Vector4 sum = Vector4.zero;
        foreach (Quaternion sample in samples)
        {
            float sign = Quaternion.Dot(reference, sample) < 0 ? -1f : 1f;
            sum += new Vector4(sample.x, sample.y, sample.z, sample.w) * sign;
        }
        return new Quaternion(sum.x, sum.y, sum.z, sum.w).normalized;
    }

    private bool PositionFresh(double timeSeconds, bool apiValid, Pose tracker,
                               bool handWristValid, Pose rawWrist)
    {
        if (!apiValid)
        {
            havePreviousTracker = false;
            positionHeldSince = -1;
            handAtHeldStartValid = false;
            return false;
        }

        bool held = false;
        if (havePreviousTracker)
        {
            double dt = timeSeconds - previousTrackerTime;
            float movement = Vector3.Distance(tracker.position, previousTrackerPosition);
            if (movement <= SamePositionEpsilonMeters)
            {
                if (positionHeldSince < 0)
                {
                    positionHeldSince = previousTrackerTime;
                    handAtHeldStartValid = handWristValid;
                    if (handWristValid) handAtHeldStart = rawWrist.position;
                }
                held = timeSeconds - positionHeldSince >= 0.5;
                if (handAtHeldStartValid && handWristValid &&
                    timeSeconds - positionHeldSince >= 0.15 &&
                    Vector3.Distance(rawWrist.position, handAtHeldStart) >= HandMovedWhileHeldMeters)
                    held = true;
            }
            else
            {
                positionHeldSince = -1;
                handAtHeldStartValid = false;
                // A reacquired position can move several centimetres in one
                // Unity frame even while the hand remains still. Wait for it
                // to settle before using it as the fused wrist root.
                if (dt > 0 && dt < 0.1 &&
                    (movement > 0.06f || movement / dt > 3.0))
                    quarantineUntil = timeSeconds + 0.35;
            }
        }
        havePreviousTracker = true;
        previousTrackerPosition = tracker.position;
        previousTrackerTime = timeSeconds;
        return !held && timeSeconds >= quarantineUntil;
    }

    public Result Process(double timeSeconds, bool trackerApiValid, Pose tracker,
                          bool handTracked, Pose[] rawJoints, bool[] jointValid)
    {
        if (rawJoints == null || rawJoints.Length != JointCount ||
            jointValid == null || jointValid.Length != JointCount)
            throw new ArgumentException("Expected 26 hand joints and validity flags");

        var merged = new Pose[JointCount];
        Array.Copy(rawJoints, merged, JointCount);
        bool wristObserved = handTracked && jointValid[WristIndex];
        bool trackerFresh = PositionFresh(timeSeconds, trackerApiValid, tracker,
                                          wristObserved, rawJoints[WristIndex]);
        int validCount = 0;
        if (handTracked)
            for (int i = 0; i < JointCount; i++) if (jointValid[i]) validCount++;

        // Estimate the rigid mount transform once per recording. After locking,
        // optical wrist noise must not move or rotate the tracker-driven root.
        if (!calibrated && trackerFresh && wristObserved)
        {
            Pose rawWrist = rawJoints[WristIndex];
            Vector3 candidate = Quaternion.Inverse(tracker.rotation) *
                                (rawWrist.position - tracker.position);
            offsetSamples.Add(candidate);
            rotationSamples.Add((Quaternion.Inverse(tracker.rotation) * rawWrist.rotation).normalized);
            if (offsetSamples.Count >= MinimumCalibrationSamples)
            {
                offset = MedianOffset(offsetSamples);
                trackerToHandRotation = AverageRotation(rotationSamples);
                relativeRotationReady = true;
                calibrated = true;
            }
        }

        string source;
        bool orientationObserved = false;
        if (trackerFresh && calibrated && relativeRotationReady)
        {
            Pose fusedWrist = new Pose(tracker.position + tracker.rotation * offset,
                                        tracker.rotation * trackerToHandRotation);
            if (wristObserved)
            {
                Quaternion inverseRawWrist = Quaternion.Inverse(rawJoints[WristIndex].rotation);
                for (int i = 0; i < JointCount; i++)
                {
                    if (!jointValid[i]) { merged[i] = MissingPose(); continue; }
                    Vector3 localPosition = inverseRawWrist *
                        (rawJoints[i].position - rawJoints[WristIndex].position);
                    Quaternion localRotation = inverseRawWrist * rawJoints[i].rotation;
                    merged[i] = new Pose(fusedWrist.position + fusedWrist.rotation * localPosition,
                                         fusedWrist.rotation * localRotation);
                }
                source = "tracker_root_pico_fingers";
                orientationObserved = true;
            }
            else
            {
                for (int i = 0; i < JointCount; i++) merged[i] = MissingPose();
                merged[WristIndex] = fusedWrist;
                source = "tracker_wrist_only";
            }
        }
        else if (handTracked)
        {
            source = trackerApiValid && !trackerFresh ? "pico_tracker_stale" :
                     trackerFresh ? "pico_calibrating" : "pico_only";
            orientationObserved = wristObserved;
        }
        else
        {
            for (int i = 0; i < JointCount; i++) merged[i] = MissingPose();
            source = trackerApiValid && !trackerFresh ? "no_hand_tracker_stale" : "no_hand_pose";
        }

        return new Result {
            joints = merged,
            source = source,
            trackerApiValid = trackerApiValid,
            trackerPositionFresh = trackerFresh,
            handTracked = handTracked,
            handOrientationObserved = orientationObserved,
            calibrationSamples = offsetSamples.Count,
            validJointCount = validCount,
            trackerLocalWristOffset = offset,
            trackerToWristRotation = trackerToHandRotation,
            calibrationFrozen = calibrated,
        };
    }
}
