using System;
using System.Collections.Generic;
using UnityEngine;

// Tracker IDs identify devices, not anatomical sides. Pair in the same
// tracking-origin coordinates as the optical wrist before calibrating a mount.
public sealed class EgoWristTrackerPairing
{
    public const int MinimumSamples = 30;
    public const float MaximumWristDistance = 0.20f;
    public const float MinimumDistanceMargin = 0.04f;
    private const double MinimumObservationSeconds = 0.30;
    private const double MaximumPositionHoldSeconds = 0.50;
    private sealed class Observation
    {
        public Vector3 position;
        public double changedAt;
    }
    private readonly Dictionary<long, Observation> observations = new Dictionary<long, Observation>();
    private long candidateId;
    private double candidateSince, previousFrameTime = -1;
    public long SelectedId { get; private set; }
    public int CandidateSamples { get; private set; }
    public float SelectedDistance { get; private set; }

    public void Reset()
    {
        SelectedId = candidateId = 0;
        CandidateSamples = 0;
        SelectedDistance = 0;
        previousFrameTime = -1;
        observations.Clear();
    }

    public long Process(double seconds, bool wristObserved, Vector3 wrist,
                        IDictionary<long, Pose> validTrackers, long reservedTrackerId = 0,
                        bool otherWristObserved = false, Vector3 otherWrist = default)
    {
        if (SelectedId != 0) return SelectedId;
        if (previousFrameTime >= 0 && (seconds <= previousFrameTime || seconds - previousFrameTime > 0.10))
            ClearCandidate();
        previousFrameTime = seconds;
        long nearest = 0;
        float closest = float.PositiveInfinity, second = float.PositiveInfinity;
        foreach (var pair in validTrackers)
        {
            if (pair.Key <= 0 || !Finite(pair.Value.position)) continue;
            Vector3 position = pair.Value.position;
            if (!observations.TryGetValue(pair.Key, out Observation observation))
            {
                observation = new Observation { position = position, changedAt = seconds };
                observations[pair.Key] = observation;
            }
            else if ((position - observation.position).sqrMagnitude > 1e-12f)
            {
                observation.position = position;
                observation.changedAt = seconds;
            }
            // Reject held API poses; they can look deceptively close to a wrist.
            if (seconds - observation.changedAt >= MaximumPositionHoldSeconds) continue;
            if (pair.Key == reservedTrackerId) continue;
            float distance = Vector3.Distance(wrist, position);
            // A tracker must be clearly closer to this wrist than to the other
            // observed wrist. Do not let update order resolve crossed-hand ties.
            if (otherWristObserved && Finite(otherWrist) &&
                Vector3.Distance(otherWrist, position) - distance < MinimumDistanceMargin)
                continue;
            if (distance < closest)
            {
                second = closest;
                closest = distance;
                nearest = pair.Key;
            }
            else if (distance < second) second = distance;
        }
        if (!wristObserved || !Finite(wrist) || nearest == 0 ||
            closest > MaximumWristDistance || second - closest < MinimumDistanceMargin)
        {
            ClearCandidate();
            return 0;
        }
        if (nearest != candidateId)
        {
            candidateId = nearest;
            candidateSince = seconds;
            CandidateSamples = 0;
        }
        CandidateSamples++;
        if (CandidateSamples >= MinimumSamples && seconds - candidateSince >= MinimumObservationSeconds)
        {
            SelectedId = nearest;
            SelectedDistance = closest;
        }
        return SelectedId;
    }

    private void ClearCandidate()
    {
        candidateId = 0;
        CandidateSamples = 0;
    }

    private static bool Finite(Vector3 position) =>
        !float.IsNaN(position.x) && !float.IsInfinity(position.x) &&
        !float.IsNaN(position.y) && !float.IsInfinity(position.y) &&
        !float.IsNaN(position.z) && !float.IsInfinity(position.z);
}
