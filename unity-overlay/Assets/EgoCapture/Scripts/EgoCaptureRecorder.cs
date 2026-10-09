using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NatSuite.Recorders.Clocks;
using Unity.XR.PXR;
using PXR_Enterprise = Unity.XR.PICO.TOBSupport.PXR_Enterprise;
using SystemFunctionSwitchEnum = Unity.XR.PICO.TOBSupport.SystemFunctionSwitchEnum;
using SwitchEnum = Unity.XR.PICO.TOBSupport.SwitchEnum;
using ScreenOffDelayTimeEnum = Unity.XR.PICO.TOBSupport.ScreenOffDelayTimeEnum;
using SleepDelayTimeEnum = Unity.XR.PICO.TOBSupport.SleepDelayTimeEnum;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;
using UnityEngine.UI;

// EgoKit-style ego video + head/hand pose capture, with an extra independent
// Motion Tracker stream and independently calibrated bilateral wrist fusion.
public sealed class EgoCaptureRecorder : MonoBehaviour
{
    private const string HandPermission = "com.picovr.permission.HAND_TRACKING";
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private static readonly XrCameraIdPICO LeftCamera = XrCameraIdPICO.XR_CAMERA_ID_RGB_LEFT_PICO;
    // XR Hands enumerates Wrist before Palm; EgoKit's poses.txt uses OpenXR order.
    private static readonly XRHandJointID[] PoseJointOrder = {
        XRHandJointID.Palm, XRHandJointID.Wrist,
        XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal,
        XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
        XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
        XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
        XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
        XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
        XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
        XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
        XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
        XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip
    };
    private readonly HashSet<long> trackerIds = new HashSet<long>();
    private readonly List<long> sortedTrackerIds = new List<long>();

    private StreamWriter posesWriter, trackerWriter, videoFrameWriter, statusWriter;
    private StreamWriter fusedPosesWriter, fusionStatusWriter, leftFusionStatusWriter;
    private readonly EgoWristFusion leftFusion = new EgoWristFusion();
    private readonly EgoWristFusion rightFusion = new EgoWristFusion();
    private readonly EgoBilateralTrackerPairing pairing = new EgoBilateralTrackerPairing();
    private readonly Dictionary<long, Pose> validTrackerPoses = new Dictionary<long, Pose>();
    private long selectedLeftWristTrackerId, selectedRightWristTrackerId;
    private string lastLeftFusionSource = "pico_only", lastFusionSource = "pico_only";
    private int leftCalibrationSamples, rightCalibrationSamples;
    private int fusedFrames, trackerStaleFrames, leftFusedFrames, leftTrackerStaleFrames;
    private EgoMp4Recorder videoRecorder;
    private XRHandSubsystem handSubsystem;
    private RealtimeClock videoClock;
    private string sessionDirectory;
    private double startMono;
    private DateTime startUtc;
    private int poseFrames, videoFrames;
    private bool paused, disposed, initializing, recording, trackerRequested, finalizing;
    private bool cameraDeviceCreated, cameraSessionCreated, cameraCapturing;
    private bool cameraResumePending;
    private bool cameraRetryScheduled;
    private int cameraGeneration;
    private float nextCameraOpenAfter;
    private int cameraWidth = 1280, cameraHeight = 960, cameraFps = 30;
    private long lastCaptureTime;
    private byte[] tightRgba;
    private string cameraStatus = "not initialized";
    private Text recordingStatusText;
    private float savedUntil;
    private int savedSeconds;
    private int previousSleepTimeout;
    private int originalAutoSleep = -1;
    private int originalProximitySensor = -1;
    private bool enterpriseBound, autoSleepChanged, proximitySensorChanged;
    private ScreenOffDelayTimeEnum originalScreenOffDelay;
    private SleepDelayTimeEnum originalSystemSleepDelay;
    private bool awakeDelaysCaptured, awakeDelaysRequested, screenOffNeverConfirmed;
    private int screenOffSetResult = -1;

    private void Awake()
    {
        Application.targetFrameRate = 90;
        previousSleepTimeout = Screen.sleepTimeout;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        PXR_Manager.EnableVideoSeeThrough = true;
        var camera = Camera.main;
        if (camera != null)
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            CreateRecordingStatus(camera);
        }
        PXR_MotionTracking.RequestMotionTrackerCompleteAction += OnTrackersReady;
        PXR_MotionTracking.MotionTrackerConnectionAction += OnTrackerConnection;
    }

    private void Start()
    {
        try
        {
            if (!PXR_Enterprise.InitEnterpriseService())
            {
                Debug.LogWarning("EgoCapture enterprise service init failed; auto sleep remains unchanged");
            }
            else PXR_Enterprise.BindEnterpriseService(bound =>
            {
                enterpriseBound = bound;
                if (!bound)
                {
                    Debug.LogWarning("EgoCapture enterprise service bind failed; auto sleep remains unchanged");
                    return;
                }
                PXR_Enterprise.GetSwitchSystemFunctionStatus(
                    SystemFunctionSwitchEnum.SFS_AUTOSLEEP, status =>
                    {
                        originalAutoSleep = status;
                        Debug.Log("EgoCapture original auto sleep status=" + status);
                    });
                PXR_Enterprise.GetSwitchSystemFunctionStatus(
                    SystemFunctionSwitchEnum.SFS_PSENSOR, status =>
                    {
                        originalProximitySensor = status;
                        Debug.Log("EgoCapture original proximity sensor status=" + status);
                    });
            });
        }
        catch (Exception e) { Debug.LogWarning("EgoCapture auto sleep setup: " + e); }
        StartCoroutine(InitializeWhenReady());
    }

    private void KeepAppAwake()
    {
        // Keep the headset awake while this app is foreground, including the
        // interval before VOL+ is pressed. Otherwise removing it during setup
        // pauses XR and can leave the PICO RGB camera unable to reopen.
        if (!enterpriseBound || paused || disposed) return;
        // The device reports switch ordinal 0 while proximity sleep is active.
        // Do not condition the OFF request on the documentation's 0/1 wording.
        if (originalAutoSleep >= 0 && originalAutoSleep < 2 && !autoSleepChanged)
        {
            try
            {
                PXR_Enterprise.SwitchSystemFunction(
                    SystemFunctionSwitchEnum.SFS_AUTOSLEEP, SwitchEnum.S_OFF);
                autoSleepChanged = true;
                Debug.Log("EgoCapture requested auto sleep OFF while app is active");
            }
            catch (Exception e) { Debug.LogWarning("EgoCapture auto sleep OFF failed: " + e); }
        }
        // Configure the PICO proximity screen-off policy itself, rather than
        // relying only on Unity/Android window keep-screen-on. Leave the physical
        // proximity sensor enabled; its readings are useful for diagnosis.
        if (!awakeDelaysRequested)
        {
            try
            {
                if (!awakeDelaysCaptured)
                {
                    originalScreenOffDelay = PXR_Enterprise.GetScreenOffDelay();
                    originalSystemSleepDelay = PXR_Enterprise.GetSleepDelay();
                    awakeDelaysCaptured = true;
                    Debug.Log("EgoCapture original PICO timeouts: screen=" + originalScreenOffDelay +
                              " sleep=" + originalSystemSleepDelay);
                }
                awakeDelaysRequested = true;
                PXR_Enterprise.PropertySetSleepDelay(SleepDelayTimeEnum.NEVER);
                PXR_Enterprise.PropertySetScreenOffDelay(ScreenOffDelayTimeEnum.NEVER, result =>
                {
                    screenOffSetResult = result;
                    screenOffNeverConfirmed = result == 0 &&
                        PXR_Enterprise.GetScreenOffDelay() == ScreenOffDelayTimeEnum.NEVER;
                    Debug.Log("EgoCapture PICO keep-awake result=" + result +
                              " screenNeverConfirmed=" + screenOffNeverConfirmed +
                              " sleep=" + PXR_Enterprise.GetSleepDelay());
                });
            }
            catch (Exception e)
            {
                // Avoid making repeated binder calls every frame after failure.
                awakeDelaysRequested = true;
                Debug.LogWarning("EgoCapture PICO never-sleep request failed: " + e);
            }
        }
    }

    private void RestoreAutoSleep()
    {
        if (!enterpriseBound) return;
        if (awakeDelaysCaptured && awakeDelaysRequested)
        {
            try
            {
                // Restore screen timeout first to avoid exceeding the restored
                // system sleep timeout. These settings are device-wide.
                PXR_Enterprise.PropertySetScreenOffDelay(originalScreenOffDelay, result =>
                    Debug.Log("EgoCapture restored screen-off timeout, result=" + result));
                PXR_Enterprise.PropertySetSleepDelay(originalSystemSleepDelay);
                awakeDelaysRequested = false;
                screenOffNeverConfirmed = false;
                Debug.Log("EgoCapture restored PICO sleep timeouts");
            }
            catch (Exception e) { Debug.LogWarning("EgoCapture timeout restore failed: " + e); }
        }
        if (proximitySensorChanged)
        {
            try
            {
                PXR_Enterprise.SwitchSystemFunction(
                    SystemFunctionSwitchEnum.SFS_PSENSOR, SwitchEnum.S_ON);
                proximitySensorChanged = false;
                Debug.Log("EgoCapture restored proximity sensor ON");
            }
            catch (Exception e) { Debug.LogWarning("EgoCapture proximity sensor restore failed: " + e); }
        }
        if (autoSleepChanged)
        {
            try
            {
                // On this device the status query returns SwitchEnum ordinal
                // values. Preserve the captured switch instead of always ON.
                PXR_Enterprise.SwitchSystemFunction(
                    SystemFunctionSwitchEnum.SFS_AUTOSLEEP, (SwitchEnum)originalAutoSleep);
                autoSleepChanged = false;
                Debug.Log("EgoCapture restored auto sleep ON");
            }
            catch (Exception e) { Debug.LogWarning("EgoCapture auto sleep restore failed: " + e); }
        }
    }

    private IEnumerator InitializeWhenReady()
    {
        if (initializing || recording || paused || disposed) yield break;
        initializing = true;

        float awakeDeadline = Time.realtimeSinceStartup + 5f;
        while (!paused && Time.realtimeSinceStartup < awakeDeadline &&
               (!enterpriseBound || originalAutoSleep < 0 || originalProximitySensor < 0))
            yield return new WaitForSecondsRealtime(0.1f);
        KeepAppAwake();
        if (paused) { initializing = false; yield break; }

        if (!Permission.HasUserAuthorizedPermission(HandPermission))
            Permission.RequestUserPermission(HandPermission);
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            Permission.RequestUserPermission(Permission.Camera);

        // Permission dialogs and the Tracker setup panel can briefly pause XR.
        float deadline = Time.realtimeSinceStartup + 20f;
        while (!paused && Time.realtimeSinceStartup < deadline &&
               !Permission.HasUserAuthorizedPermission(Permission.Camera))
            yield return new WaitForSecondsRealtime(0.25f);
        if (paused) { initializing = false; yield break; }
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            cameraStatus = "camera permission required";
            initializing = false;
            yield break;
        }

        yield return new WaitForSecondsRealtime(1f);
        if (paused) { initializing = false; yield break; }

        try
        {
            if (!trackerRequested)
            {
                trackerRequested = true;
                int requestResult = PXR_MotionTracking.CheckMotionTrackerNumber(MotionTrackerNum.TWO);
                Debug.Log("EgoCapture tracker request result=" + requestResult);
            }
        }
        catch (Exception e) { Debug.LogWarning("EgoCapture tracker request: " + e); }

        if (paused) { initializing = false; yield break; }
        // Android resumes the activity before the PICO XR session reaches FOCUSED.
        // Opening RGB in that gap can leave the native camera stuck in OPENING.
        while (!paused && !disposed && Time.realtimeSinceStartup < nextCameraOpenAfter)
            yield return new WaitForSecondsRealtime(0.1f);
        if (paused || disposed) { initializing = false; yield break; }
        InitializeCameraAsync(cameraGeneration);
    }

    private async void InitializeCameraAsync(int generation)
    {
        bool nativeCameraStuck = false;
        try
        {
            PxrResult result = PxrResult.Unknown;
            XrCameraIdPICO[] cameras = null;
            for (int attempt = 0; attempt < 10 && !paused && !disposed; attempt++)
            {
                result = PXR_CameraImage.GetAvailableCameras(out cameras);
                if (result == PxrResult.SUCCESS && cameras != null &&
                    Array.IndexOf(cameras, LeftCamera) >= 0) break;
                await System.Threading.Tasks.Task.Delay(500);
            }
            if (paused || disposed || generation != cameraGeneration) return;
            if (result != PxrResult.SUCCESS || cameras == null ||
                Array.IndexOf(cameras, LeftCamera) < 0)
            {
                cameraStatus = "left RGB camera unavailable: " + result;
                Debug.LogWarning("EgoCapture " + cameraStatus);
                return;
            }

            if (PXR_CameraImage.GetCameraImageResolutionCapability(LeftCamera, out var resolutions)
                == PxrResult.SUCCESS && resolutions != null && resolutions.Length > 0)
            {
                var best = resolutions[0];
                foreach (var size in resolutions)
                    if (size.width == 1280 && size.height == 960) { best = size; break; }
                cameraWidth = best.width;
                cameraHeight = best.height;
            }
            if (PXR_CameraImage.GetCameraImageFpsCapability(LeftCamera, out var rates)
                == PxrResult.SUCCESS && rates != null &&
                Array.IndexOf(rates, XrCameraImageFpsPICO.XR_CAMERA_IMAGE_FPS_60_PICO) >= 0)
                cameraFps = 60;

            // Rapidly reopening the camera after a prior session can transiently fail.
            int validationFailures = 0;
            for (int attempt = 0; attempt < 8 && !paused && !disposed; attempt++)
            {
                result = await PXR_CameraImage.CreateCameraDeviceAsync(LeftCamera);
                if (paused || disposed || generation != cameraGeneration)
                {
                    try { PXR_CameraImage.DestroyCameraDevice(LeftCamera); }
                    catch (Exception e) { Debug.LogWarning("EgoCapture cancelled-open cleanup: " + e); }
                    return;
                }
                if (result == PxrResult.SUCCESS) break;
                // A failed asynchronous open can leave the native camera state busy.
                try { PXR_CameraImage.DestroyCameraDevice(LeftCamera); }
                catch (Exception e) { Debug.LogWarning("EgoCapture failed-open cleanup: " + e); }
                validationFailures = result == PxrResult.ERROR_VALIDATION_FAILURE
                    ? validationFailures + 1 : 0;
                if (validationFailures >= 2)
                {
                    nativeCameraStuck = true;
                    break;
                }
                await System.Threading.Tasks.Task.Delay(1000);
            }
            if (result == PxrResult.SUCCESS) cameraDeviceCreated = true;
            if (paused || disposed || generation != cameraGeneration) { CloseCamera(); return; }
            if (result != PxrResult.SUCCESS)
            {
                cameraStatus = "CreateCameraDevice: " + result;
                Debug.LogWarning("EgoCapture " + cameraStatus);
                return;
            }
            result = await PXR_CameraImage.CreateCameraCaptureSessionAsync(
                LeftCamera, cameraWidth, cameraHeight,
                cameraFps == 60 ? XrCameraImageFpsPICO.XR_CAMERA_IMAGE_FPS_60_PICO
                                : XrCameraImageFpsPICO.XR_CAMERA_IMAGE_FPS_30_PICO,
                XrCameraImageFormatPICO.XR_CAMERA_IMAGE_FORMAT_RGBA_8888_PICO,
                XrCameraDataTransferTypePICO.XR_CAMERA_DATA_TRANSFER_TYPE_RAW_BUFFER_PICO,
                XrCameraModelPICO.XR_CAMERA_MODEL_PINHOLE_PICO);
            if (result == PxrResult.SUCCESS) cameraSessionCreated = true;
            if (paused || disposed || generation != cameraGeneration) { CloseCamera(); return; }
            if (result != PxrResult.SUCCESS)
            {
                cameraStatus = "CreateCameraCaptureSession: " + result;
                Debug.LogWarning("EgoCapture " + cameraStatus);
                CloseCamera();
                return;
            }
            result = PXR_CameraImage.BeginCameraCapture(LeftCamera);
            if (result != PxrResult.SUCCESS)
            {
                cameraStatus = "BeginCameraCapture: " + result;
                Debug.LogWarning("EgoCapture " + cameraStatus);
                CloseCamera();
                return;
            }
            cameraCapturing = true;
            cameraStatus = "left RGB " + cameraWidth + "x" + cameraHeight + " @ " + cameraFps;
            Debug.Log("EgoCapture ready; volume+ starts, volume- stops. " + cameraStatus);
            MarkCameraReady();
        }
        catch (Exception e)
        {
            cameraStatus = "camera exception: " + e.GetType().Name + " " + e.Message;
            Debug.LogError("EgoCapture " + e);
            CloseCamera();
        }
        finally
        {
            initializing = false;
            if (!cameraCapturing && !paused && !disposed && !recording)
            {
                bool restarting = nativeCameraStuck && !finalizing &&
                    TryRestartAfterCameraFailure();
                if (!restarting) ScheduleCameraRetry();
            }
        }
    }

    private void MarkCameraReady()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                activity.Call("markCameraReady");
        }
        catch (Exception e) { Debug.LogWarning("EgoCapture camera recovery reset: " + e); }
#endif
    }

    private bool TryRestartAfterCameraFailure()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                bool scheduled = activity.Call<bool>("restartAfterCameraFailure");
                if (scheduled) cameraStatus = "reopening app to reset camera";
                return scheduled;
            }
        }
        catch (Exception e) { Debug.LogWarning("EgoCapture camera process recovery: " + e); }
#endif
        return false;
    }

    private void ScheduleCameraRetry()
    {
        if (cameraRetryScheduled || cameraCapturing || paused || disposed) return;
        StartCoroutine(RetryCameraAfterDelay());
    }

    private IEnumerator RetryCameraAfterDelay()
    {
        cameraRetryScheduled = true;
        Debug.LogWarning("EgoCapture camera will retry in 3 seconds: " + cameraStatus);
        yield return new WaitForSecondsRealtime(3f);
        cameraRetryScheduled = false;
        if (!cameraCapturing && !initializing && !recording && !paused && !disposed)
            StartCoroutine(InitializeWhenReady());
    }

    private void StartRecording()
    {
        if (paused || recording || finalizing || cameraResumePending || !cameraCapturing) return;
        savedUntil = 0;
        sessionDirectory = Path.Combine(Application.persistentDataPath, "Recordings",
            DateTime.Now.ToString("yyyyMMdd_HHmmss", C));
        Directory.CreateDirectory(sessionDirectory);
        posesWriter = CreateWriter("poses.txt");
        trackerWriter = CreateWriter("tracker.txt");
        videoFrameWriter = CreateWriter("video_frames.txt");
        statusWriter = CreateWriter("tracking_status.txt");
        fusedPosesWriter = CreateWriter("poses_fused.txt");
        fusionStatusWriter = CreateWriter("fusion_status.txt");
        leftFusionStatusWriter = CreateWriter("fusion_status_left.txt");
        posesWriter.WriteLine("# idx t_ms head_pos(x y z) head_rot(x y z w) left[26]:(pos+rot) right[26]:(pos+rot)");
        posesWriter.WriteLine("# joints in OpenXR XR_HAND_JOINT order: 0=Palm 1=Wrist 2..5=Thumb(M,P,D,T) 6..10=Index(M,P,I,D,T) 11..15=Middle 16..20=Ring 21..25=Little");
        trackerWriter.WriteLine("# pose_idx t_ms tracker_id result valid x y z qx qy qz qw (tracking-origin coordinates; no wrist calibration)");
        videoFrameWriter.WriteLine("# frame t_ms_from_start encoder_pts_ns camera_capture_time_raw unity_monotonic_seconds utc_ticks");
        statusWriter.WriteLine("# pose_idx t_ms left_xrhands_subsystem left_tracked left_joint_count right_xrhands_subsystem right_tracked right_joint_count tracker_count");
        fusedPosesWriter.WriteLine("# Experimental bilateral wrist fusion; same head/left[26]/right[26] layout as poses.txt; 0=Palm 1=Wrist; check fusion_status_left.txt for left and fusion_status.txt for right before using a frame");
        fusionStatusWriter.WriteLine("# pose_idx t_ms source tracker_id tracker_api_valid tracker_position_fresh pico_right_tracked valid_right_joints calibration_samples hand_orientation_observed offset_x_m offset_y_m offset_z_m calibration_frozen tracker_to_wrist_qx tracker_to_wrist_qy tracker_to_wrist_qz tracker_to_wrist_qw");
        leftFusionStatusWriter.WriteLine("# pose_idx t_ms source tracker_id tracker_api_valid tracker_position_fresh pico_left_tracked valid_left_joints calibration_samples hand_orientation_observed offset_x_m offset_y_m offset_z_m calibration_frozen tracker_to_wrist_qx tracker_to_wrist_qy tracker_to_wrist_qz tracker_to_wrist_qw");
        leftFusion.Reset();
        rightFusion.Reset();
        pairing.Reset();
        selectedLeftWristTrackerId = selectedRightWristTrackerId = 0;
        lastLeftFusionSource = lastFusionSource = "pico_only";
        leftCalibrationSamples = rightCalibrationSamples = 0;
        fusedFrames = trackerStaleFrames = leftFusedFrames = leftTrackerStaleFrames = 0;
        startMono = Time.realtimeSinceStartupAsDouble;
        startUtc = DateTime.UtcNow;
        poseFrames = videoFrames = 0;
        lastCaptureTime = 0;

        if (cameraCapturing)
        {
            try
            {
                videoRecorder = new EgoMp4Recorder(Path.Combine(sessionDirectory, "internal.mp4"),
                    cameraWidth, cameraHeight, cameraFps, 8_000_000);
                videoClock = new RealtimeClock();
            }
            catch (Exception e)
            {
                cameraStatus = "encoder exception: " + e.GetType().Name + " " + e.Message;
                Debug.LogError("EgoCapture " + e);
            }
        }
        File.WriteAllText(Path.Combine(sessionDirectory, "log.txt"),
            "EgoCapture session started: " + startUtc.ToString("O", C) + "\n" +
            "Device: " + SystemInfo.deviceModel + "\n" +
            "Application version: " + Application.version + "\n" +
            "Camera: " + cameraStatus + "\n" +
            "PICO hand tracking: " + Permission.HasUserAuthorizedPermission(HandPermission) + "\n" +
            "Hand pose source: Unity XR Hands / PICO Hands subsystem.\n" +
            "Keep-awake requested: auto_sleep_off=" + autoSleepChanged +
            ", proximity_sensor_off=" + proximitySensorChanged + "\n" +
            "PICO screen-off NEVER confirmed=" + screenOffNeverConfirmed +
            ", screen-off set result=" + screenOffSetResult +
            ", original auto-sleep switch ordinal=" + originalAutoSleep + "\n" +
            "Raw Tracker and PICO hand streams are unchanged. Experimental bilateral fusion is in poses_fused.txt; left status in fusion_status_left.txt, right status in fusion_status.txt.\n" +
            "Bilateral Tracker selection: each wrist needs a nearby fresh Tracker within 0.20 m, a 0.04 m margin over competing devices and the other visible wrist, 30 samples and 0.30 s. One-to-one assignments; no numeric ID preference. IDs lock per recording and are logged per hand.\n" +
            "Fusion mode: locked rigid mount. Position and rotation offsets freeze after " + EgoWristFusion.MinimumCalibrationSamples + " valid paired samples; independently per hand; reset on each recording.\n" +
            "Keep both wrists apart and visible at recording start; a slipped mount or wrist flexion relative to a forearm-mounted Tracker breaks the rigid calibration. Stale/jump fallback to PICO. Not validated as an absolute accuracy improvement.\n");
        recording = true;
        KeepAppAwake();
        Debug.Log("EgoCapture recording: " + sessionDirectory);
    }

    private StreamWriter CreateWriter(string filename) =>
        new StreamWriter(Path.Combine(sessionDirectory, filename), false, new UTF8Encoding(false), 65536);

    private void Update()
    {
        KeepAppAwake();
        UpdateRecordingStatus();
        if (!recording || paused) return;
        WritePoseAndTrackers();
        if (cameraCapturing && videoRecorder != null) WriteVideoFrame();
        if (poseFrames % 90 == 0)
        {
            posesWriter.Flush();
            trackerWriter.Flush();
            statusWriter.Flush();
            videoFrameWriter.Flush();
            fusedPosesWriter.Flush();
            fusionStatusWriter.Flush();
            leftFusionStatusWriter.Flush();
        }
    }

    private void CreateRecordingStatus(Camera camera)
    {
        var panel = new GameObject("RecordingStatusHUD", typeof(RectTransform),
            typeof(Canvas), typeof(CanvasRenderer), typeof(Image));
        panel.transform.SetParent(camera.transform, false);
        var rect = panel.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(900, 160);
        rect.localPosition = new Vector3(0f, 0.34f, 1.4f);
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * 0.001f;
        var canvas = panel.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = camera;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 100;
        var background = panel.GetComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.68f);
        background.raycastTarget = false;

        var label = new GameObject("RecordingStatusText", typeof(RectTransform),
            typeof(CanvasRenderer), typeof(Text));
        label.transform.SetParent(panel.transform, false);
        var labelRect = label.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(18, 0);
        labelRect.offsetMax = new Vector2(-18, 0);
        recordingStatusText = label.GetComponent<Text>();
        recordingStatusText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        recordingStatusText.fontSize = 30;
        recordingStatusText.alignment = TextAnchor.MiddleLeft;
        recordingStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;
        recordingStatusText.raycastTarget = false;
        recordingStatusText.color = Color.white;
        recordingStatusText.text = "CAMERA STARTING...";
    }

    private void UpdateRecordingStatus()
    {
        if (recordingStatusText == null) return;
        if (recording)
        {
            int seconds = Math.Max(0, (int)(Time.realtimeSinceStartupAsDouble - startMono));
            recordingStatusText.text = "REC  " + (seconds / 60).ToString("00") + ":" +
                (seconds % 60).ToString("00") + "  |  VOL- STOP\n" +
                HandStatusLabel("LEFT", selectedLeftWristTrackerId, lastLeftFusionSource,
                    pairing.Left.CandidateSamples, leftCalibrationSamples) + "\n" +
                HandStatusLabel("RIGHT", selectedRightWristTrackerId, lastFusionSource,
                    pairing.Right.CandidateSamples, rightCalibrationSamples);
            recordingStatusText.color = new Color(1f, 0.4f, 0.35f);
        }
        else if (finalizing)
        {
            recordingStatusText.text = "SAVING VIDEO...";
            recordingStatusText.color = Color.yellow;
        }
        else if (Time.realtimeSinceStartup < savedUntil)
        {
            recordingStatusText.text = "SAVED  " + (savedSeconds / 60).ToString("00") +
                ":" + (savedSeconds % 60).ToString("00") + "  |  VOL+ START";
            recordingStatusText.color = Color.green;
        }
        else if (cameraResumePending)
        {
            recordingStatusText.text = "CAMERA RESUMING...";
            recordingStatusText.color = Color.yellow;
        }
        else if (cameraCapturing)
        {
            recordingStatusText.text = "READY  |  VOL+ START";
            recordingStatusText.color = Color.white;
        }
        else
        {
            recordingStatusText.text = cameraStatus == "reopening app to reset camera" ?
                "CAMERA RECOVERING..." :
                initializing ? "CAMERA STARTING..." :
                cameraRetryScheduled ? "CAMERA RETRYING..." :
                "CAMERA NOT READY  |  VOL+ RETRY";
            recordingStatusText.color = Color.yellow;
        }
    }

    private string HandStatusLabel(string side, long id, string source, int samples, int calibration)
    {
        if (id == 0) return side + (trackerIds.Count == 0 ? "  PICO (NO TRACKER)" :
            "  PAIR " + samples + "/" + EgoWristTrackerPairing.MinimumSamples + "  KEEP HANDS APART");
        string state = source == "tracker_root_pico_fingers" ? "FUSED LOCKED" :
                       source == "tracker_wrist_only" ? "WRIST ONLY" :
                       source == "pico_calibrating" ? "CAL " + calibration + "/" + EgoWristFusion.MinimumCalibrationSamples :
                       source.Contains("stale") ? "PICO (STALE)" :
                       source == "no_hand_pose" ? "NO HAND / TRACKER LOST" : "PICO (TRACKER LOST)";
        return side + "  Tracker " + id + "  " + state;
    }

    // Called by EgoCaptureActivity, which consumes Android's hardware volume keys.
    public void OnVolumeButton(string button)
    {
        Debug.Log("EgoCapture volume button: " + button);
        if (paused) return;
        if (button == "up")
        {
            if (recording) return;
            if (finalizing) return;
            if (cameraResumePending) return;
            if (cameraCapturing && !cameraResumePending) StartRecording();
            else if (!initializing) StartCoroutine(InitializeWhenReady());
        }
        else if (button == "down")
        {
            if (recording) StopRecording();
        }
    }

    private void WritePoseAndTrackers()
    {
        long tMs = (long)Math.Round((Time.realtimeSinceStartupAsDouble - startMono) * 1000.0);
        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        Vector3 hp = Vector3.zero;
        Quaternion hq = Quaternion.identity;
        bool headValid = head.TryGetFeatureValue(CommonUsages.devicePosition, out hp) &&
                         head.TryGetFeatureValue(CommonUsages.deviceRotation, out hq);
        if (handSubsystem == null || !handSubsystem.running)
        {
            var loader = XRGeneralSettings.Instance?.Manager?.activeLoader;
            handSubsystem = loader?.GetLoadedSubsystem<XRHandSubsystem>();
        }
        bool handsReady = handSubsystem != null && handSubsystem.running;
        XRHand left = handsReady ? handSubsystem.leftHand : default;
        XRHand right = handsReady ? handSubsystem.rightHand : default;
        bool leftActive = handsReady && left.isTracked;
        bool rightActive = handsReady && right.isTracked;

        var line = new StringBuilder(4096);
        line.Append(poseFrames).Append(' ').Append(tMs);
        AppendPose(line, headValid ? hp : Vector3.zero, headValid ? hq : Quaternion.identity);
        var leftJoints = new Pose[PoseJointOrder.Length];
        var rightJoints = new Pose[PoseJointOrder.Length];
        var leftJointValid = new bool[PoseJointOrder.Length];
        var rightJointValid = new bool[PoseJointOrder.Length];
        for (int i = 0; i < PoseJointOrder.Length; i++)
            leftJointValid[i] = AppendJoint(line, left, leftActive, PoseJointOrder[i], out leftJoints[i]);
        for (int i = 0; i < PoseJointOrder.Length; i++)
            rightJointValid[i] = AppendJoint(line, right, rightActive,
                                             PoseJointOrder[i], out rightJoints[i]);
        posesWriter.WriteLine(line);

        sortedTrackerIds.Clear();
        sortedTrackerIds.AddRange(trackerIds);
        sortedTrackerIds.Sort();
        validTrackerPoses.Clear();
        foreach (long id in sortedTrackerIds)
        {
            var location = new MotionTrackerLocation();
            bool valid = false;
            int result = PXR_MotionTracking.GetMotionTrackerLocation(id, ref location, ref valid);
            if (result == 0 && valid)
                validTrackerPoses[id] = new Pose(location.pose.Position.ToVector3(),
                                                location.pose.Orientation.ToQuat());
            var row = new StringBuilder(160);
            row.Append(poseFrames).Append(' ').Append(tMs).Append(' ').Append(id)
               .Append(' ').Append(result).Append(' ').Append(valid ? 1 : 0);
            AppendPose(row, result == 0 && valid ? location.pose.Position.ToVector3() : Vector3.zero,
                            result == 0 && valid ? location.pose.Orientation.ToQuat() : Quaternion.identity);
            trackerWriter.WriteLine(row);
        }
        pairing.Process(tMs / 1000.0, leftActive && leftJointValid[1], leftJoints[1].position,
            rightActive && rightJointValid[1], rightJoints[1].position, validTrackerPoses);
        LogNewPairing("left", ref selectedLeftWristTrackerId, pairing.Left, tMs);
        LogNewPairing("right", ref selectedRightWristTrackerId, pairing.Right, tMs);
        Pose leftTrackerPose = new Pose(Vector3.zero, Quaternion.identity);
        Pose rightTrackerPose = new Pose(Vector3.zero, Quaternion.identity);
        bool leftTrackerApiValid = selectedLeftWristTrackerId != 0 &&
            validTrackerPoses.TryGetValue(selectedLeftWristTrackerId, out leftTrackerPose);
        bool rightTrackerApiValid = selectedRightWristTrackerId != 0 &&
            validTrackerPoses.TryGetValue(selectedRightWristTrackerId, out rightTrackerPose);
        var fusedLeft = leftFusion.Process(tMs / 1000.0, leftTrackerApiValid,
            leftTrackerPose, leftActive, leftJoints, leftJointValid);
        var fused = rightFusion.Process(tMs / 1000.0, rightTrackerApiValid,
            rightTrackerPose, rightActive, rightJoints, rightJointValid);
        lastLeftFusionSource = fusedLeft.source;
        lastFusionSource = fused.source;
        leftCalibrationSamples = fusedLeft.calibrationSamples;
        rightCalibrationSamples = fused.calibrationSamples;
        if (fusedLeft.source == "tracker_root_pico_fingers") leftFusedFrames++;
        if (fused.source == "tracker_root_pico_fingers") fusedFrames++;
        if (leftTrackerApiValid && !fusedLeft.trackerPositionFresh) leftTrackerStaleFrames++;
        if (rightTrackerApiValid && !fused.trackerPositionFresh) trackerStaleFrames++;
        var fusedLine = new StringBuilder(4096);
        fusedLine.Append(poseFrames).Append(' ').Append(tMs);
        AppendPose(fusedLine, headValid ? hp : Vector3.zero, headValid ? hq : Quaternion.identity);
        for (int i = 0; i < PoseJointOrder.Length; i++)
            AppendPose(fusedLine, fusedLeft.joints[i].position, fusedLeft.joints[i].rotation);
        for (int i = 0; i < PoseJointOrder.Length; i++)
            AppendPose(fusedLine, fused.joints[i].position, fused.joints[i].rotation);
        fusedPosesWriter.WriteLine(fusedLine);
        WriteFusionStatus(leftFusionStatusWriter, poseFrames, tMs, selectedLeftWristTrackerId, fusedLeft);
        WriteFusionStatus(fusionStatusWriter, poseFrames, tMs, selectedRightWristTrackerId, fused);
        statusWriter.WriteLine(poseFrames + " " + tMs + " " + (handsReady ? 1 : 0) + " " +
            (leftActive ? 1 : 0) + " " + CountValid(leftJointValid) + " " +
            (handsReady ? 1 : 0) + " " + (rightActive ? 1 : 0) + " " +
            CountValid(rightJointValid) + " " + sortedTrackerIds.Count);
        poseFrames++;
    }

    private void LogNewPairing(string side, ref long selectedId, EgoWristTrackerPairing handPairing, long tMs)
    {
        if (handPairing.SelectedId == 0 || selectedId == handPairing.SelectedId) return;
        selectedId = handPairing.SelectedId;
        string message = "Selected " + side + " wrist Tracker ID: " + selectedId +
            " (wrist proximity confirmed, distance_m=" + handPairing.SelectedDistance.ToString("R", C) +
            ", t_ms=" + tMs + ")";
        File.AppendAllText(Path.Combine(sessionDirectory, "log.txt"), message + "\n");
        Debug.Log("EgoCapture " + message);
    }

    private static int CountValid(bool[] flags)
    {
        int count = 0;
        foreach (bool valid in flags) if (valid) count++;
        return count;
    }

    private static void WriteFusionStatus(StreamWriter writer, int frame, long tMs,
                                          long trackerId, EgoWristFusion.Result fused)
    {
        writer.WriteLine(frame + " " + tMs + " " + fused.source + " " +
            trackerId + " " + (fused.trackerApiValid ? 1 : 0) + " " +
            (fused.trackerPositionFresh ? 1 : 0) + " " +
            (fused.handTracked ? 1 : 0) + " " + fused.validJointCount + " " +
            fused.calibrationSamples + " " + (fused.handOrientationObserved ? 1 : 0) + " " +
            fused.trackerLocalWristOffset.x.ToString("R", C) + " " +
            fused.trackerLocalWristOffset.y.ToString("R", C) + " " +
            fused.trackerLocalWristOffset.z.ToString("R", C) + " " +
            (fused.calibrationFrozen ? 1 : 0) + " " +
            fused.trackerToWristRotation.x.ToString("R", C) + " " +
            fused.trackerToWristRotation.y.ToString("R", C) + " " +
            fused.trackerToWristRotation.z.ToString("R", C) + " " +
            fused.trackerToWristRotation.w.ToString("R", C));
    }

    private static bool AppendJoint(StringBuilder row, XRHand hand, bool active,
                                    XRHandJointID jointId, out Pose pose)
    {
        if (!active || !hand.GetJoint(jointId).TryGetPose(out pose))
        {
            pose = new Pose(Vector3.zero, Quaternion.identity);
            AppendPose(row, Vector3.zero, Quaternion.identity);
            return false;
        }
        AppendPose(row, pose.position, pose.rotation);
        return true;
    }

    private static void AppendPose(StringBuilder row, Vector3 p, Quaternion q)
    {
        row.Append(' ').Append(p.x.ToString("R", C)).Append(' ').Append(p.y.ToString("R", C))
           .Append(' ').Append(p.z.ToString("R", C)).Append(' ').Append(q.x.ToString("R", C))
           .Append(' ').Append(q.y.ToString("R", C)).Append(' ').Append(q.z.ToString("R", C))
           .Append(' ').Append(q.w.ToString("R", C));
    }

    private unsafe void WriteVideoFrame()
    {
        var result = PXR_CameraImage.AcquireCameraImage(LeftCamera, lastCaptureTime,
                                                         out ulong imageId, out long captureTime);
        if (result != PxrResult.SUCCESS) return;
        try
        {
            result = PXR_CameraImage.GetCameraImageData(LeftCamera, imageId, out var raw);
            if (result != PxrResult.SUCCESS || raw.buffer == IntPtr.Zero ||
                raw.width != cameraWidth || raw.height != cameraHeight || raw.bytesPerPixel != 4)
                return;

            long pts = videoClock.timestamp;
            if (raw.stride == raw.width * 4 && raw.bufferSize >= raw.stride * raw.height)
                videoRecorder.CommitFrame(raw.buffer.ToPointer(), pts);
            else
            {
                int rowBytes = cameraWidth * 4;
                if (tightRgba == null || tightRgba.Length != rowBytes * cameraHeight)
                    tightRgba = new byte[rowBytes * cameraHeight];
                for (int y = 0; y < cameraHeight; y++)
                    Marshal.Copy(IntPtr.Add(raw.buffer, y * (int)raw.stride),
                                 tightRgba, y * rowBytes, rowBytes);
                fixed (byte* ptr = tightRgba) videoRecorder.CommitFrame(ptr, pts);
            }
            lastCaptureTime = captureTime;
            videoFrameWriter.WriteLine(videoFrames + " " +
                (long)Math.Round((Time.realtimeSinceStartupAsDouble - startMono) * 1000.0) + " " +
                pts + " " + captureTime + " " +
                Time.realtimeSinceStartupAsDouble.ToString("R", C) + " " + DateTime.UtcNow.Ticks);
            videoFrames++;
        }
        catch (Exception e)
        {
            Debug.LogError("EgoCapture video frame: " + e);
            cameraStatus = "video frame exception: " + e.GetType().Name;
            cameraCapturing = false;
        }
        finally { PXR_CameraImage.ReleaseCameraImage(LeftCamera, imageId); }
    }

    private void OnTrackersReady(RequestMotionTrackerCompleteEventData data)
    {
        trackerIds.Clear();
        if (data.trackerIds != null)
            for (int i = 0; i < Math.Min(data.trackerIds.Length, (int)data.trackerCount); i++)
                trackerIds.Add(data.trackerIds[i]);
        Debug.Log("EgoCapture tracker IDs: " + string.Join(",", trackerIds));
    }

    private void OnTrackerConnection(long id, int state)
    {
        if (state == 1) trackerIds.Add(id);
        else trackerIds.Remove(id);
        Debug.Log("EgoCapture tracker " + id + " state=" + state);
    }

    private async void StopRecording()
    {
        if (!recording) return;
        recording = false;
        finalizing = true;
        savedSeconds = Math.Max(0, (int)(Time.realtimeSinceStartupAsDouble - startMono));
        posesWriter?.Dispose(); trackerWriter?.Dispose();
        videoFrameWriter?.Dispose(); statusWriter?.Dispose();
        fusedPosesWriter?.Dispose(); fusionStatusWriter?.Dispose(); leftFusionStatusWriter?.Dispose();
        posesWriter = trackerWriter = videoFrameWriter = statusWriter = null;
        fusedPosesWriter = fusionStatusWriter = leftFusionStatusWriter = null;
        string logPath = Path.Combine(sessionDirectory, "log.txt");
        File.AppendAllText(logPath, "Ended: " + DateTime.UtcNow.ToString("O", C) + "\n" +
            "Pose frames: " + poseFrames + "\nVideo frames: " + videoFrames + "\n" +
            "Full fused left-hand frames: " + leftFusedFrames + "\n" +
            "Full fused right-hand frames: " + fusedFrames + "\n" +
            "Left Tracker API-valid but position rejected frames: " + leftTrackerStaleFrames + "\n" +
            "Tracker API-valid but position rejected frames: " + trackerStaleFrames + "\n" +
            "Camera final status: " + cameraStatus + "\n");
        var recorder = videoRecorder;
        videoRecorder = null;
        try
        {
            if (recorder != null)
            {
                string path = await recorder.FinishWriting();
                File.AppendAllText(logPath, "Video saved: " + path + "\n");
                try
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                    {
                        string name = "EgoTracker_" + Path.GetFileName(sessionDirectory) + ".mp4";
                        string galleryUri = activity.Call<string>("publishVideoToGallery", path, name);
                        File.AppendAllText(logPath, "Gallery copy: " + galleryUri + "\n");
                    }
#endif
                }
                catch (Exception e)
                {
                    File.AppendAllText(logPath, "Gallery copy failed: " + e + "\n");
                    Debug.LogWarning("EgoCapture gallery copy: " + e);
                }
            }
        }
        catch (Exception e)
        {
            File.AppendAllText(logPath, "Video finalization failed: " + e + "\n");
        }
        finally
        {
            if (paused || disposed) RestoreAutoSleep();
            finalizing = false;
            savedUntil = Time.realtimeSinceStartup + 5f;
        }
        Debug.Log("EgoCapture stopped: " + sessionDirectory);
    }

    private void CloseCamera()
    {
        cameraResumePending = false;
        try { if (cameraCapturing) PXR_CameraImage.EndCameraCapture(LeftCamera); }
        catch (Exception e) { Debug.LogWarning("EgoCapture camera end: " + e); }
        try { if (cameraSessionCreated) PXR_CameraImage.DestroyCameraCaptureSession(LeftCamera); }
        catch (Exception e) { Debug.LogWarning("EgoCapture camera session cleanup: " + e); }
        try { if (cameraDeviceCreated) PXR_CameraImage.DestroyCameraDevice(LeftCamera); }
        catch (Exception e) { Debug.LogWarning("EgoCapture camera device cleanup: " + e); }
        cameraCapturing = cameraSessionCreated = cameraDeviceCreated = false;
    }

    private void OnApplicationPause(bool isPaused)
    {
        paused = isPaused;
        if (isPaused)
        {
            cameraGeneration++;
            if (recording) StopRecording();
            else RestoreAutoSleep();
            // PICO's XR runtime pauses the RGB camera with the XR session.
            // Keep a working capture session alive here and check it on resume;
            // explicitly destroying it during pause made reopen fail on this device.
            if (!cameraCapturing) CloseCamera();
            else cameraResumePending = true;
        }
        else
        {
            KeepAppAwake();
            if (cameraCapturing)
                StartCoroutine(CheckCameraAfterResume());
            else if (!initializing && !recording)
            {
                nextCameraOpenAfter = Time.realtimeSinceStartup + 0.5f;
                StartCoroutine(InitializeWhenReady());
            }
        }
    }

    private IEnumerator CheckCameraAfterResume()
    {
        // The native session may resume slightly after Unity receives the
        // activity callback. Require two advancing capture timestamps so an
        // image buffered before sleep cannot falsely mark the camera ready.
        long firstCaptureTime = 0;
        float deadline = Time.realtimeSinceStartup + 2f;
        while (!paused && !disposed && Time.realtimeSinceStartup < deadline)
        {
            var result = PXR_CameraImage.AcquireCameraImage(LeftCamera, firstCaptureTime,
                out ulong imageId, out long captureTime);
            if (result == PxrResult.SUCCESS)
            {
                PXR_CameraImage.ReleaseCameraImage(LeftCamera, imageId);
                if (firstCaptureTime != 0 && captureTime > firstCaptureTime)
                {
                    cameraResumePending = false;
                    cameraStatus = "left RGB resumed";
                    Debug.Log("EgoCapture camera resumed without rebuilding device");
                    yield break;
                }
                firstCaptureTime = captureTime;
            }
            yield return new WaitForSecondsRealtime(0.2f);
        }
        if (paused || disposed) yield break;
        Debug.LogWarning("EgoCapture retained camera has no fresh frames after resume; rebuilding");
        CloseCamera();
        nextCameraOpenAfter = Time.realtimeSinceStartup + 0.5f;
        if (!initializing && !recording) StartCoroutine(InitializeWhenReady());
    }

    private void OnDestroy()
    {
        disposed = true;
        cameraGeneration++;
        RestoreAutoSleep();
        Screen.sleepTimeout = previousSleepTimeout;
        PXR_MotionTracking.RequestMotionTrackerCompleteAction -= OnTrackersReady;
        PXR_MotionTracking.MotionTrackerConnectionAction -= OnTrackerConnection;
        StopRecording();
        CloseCamera();
    }
}
