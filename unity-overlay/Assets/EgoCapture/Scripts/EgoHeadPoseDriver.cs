using UnityEngine;
using UnityEngine.XR;

// Keeps the empty passthrough scene's camera aligned with the XR head device.
public sealed class EgoHeadPoseDriver : MonoBehaviour
{
    private void Update()
    {
        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (head.TryGetFeatureValue(CommonUsages.devicePosition, out var position))
            transform.localPosition = position;
        if (head.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation))
            transform.localRotation = rotation;
    }
}
