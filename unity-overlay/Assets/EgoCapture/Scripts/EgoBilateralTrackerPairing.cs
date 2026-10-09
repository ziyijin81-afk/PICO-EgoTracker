using System.Collections.Generic;
using UnityEngine;

// One recording-local assignment shared by both hands. IDs are device IDs,
// never assumed to mean left or right. Locked assignments survive crossings
// and temporary tracking loss; a new recording starts a new assignment.
public sealed class EgoBilateralTrackerPairing
{
    public readonly EgoWristTrackerPairing Left = new EgoWristTrackerPairing();
    public readonly EgoWristTrackerPairing Right = new EgoWristTrackerPairing();

    public void Reset()
    {
        Left.Reset();
        Right.Reset();
    }

    public void Process(double seconds, bool leftObserved, Vector3 leftWrist,
                        bool rightObserved, Vector3 rightWrist,
                        IDictionary<long, Pose> validTrackers)
    {
        Left.Process(seconds, leftObserved, leftWrist, validTrackers,
                     Right.SelectedId, rightObserved, rightWrist);
        Right.Process(seconds, rightObserved, rightWrist, validTrackers,
                      Left.SelectedId, leftObserved, leftWrist);
    }
}
