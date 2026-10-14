using System;
using System.IO;
using Unity.XR.PXR;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class EgoCaptureBuild
{
    public static void BuildAndroid()
    {
        EgoFusionSelfTest.Run();
        EgoFusionSelfTest.ReplayFromCommandLine();
        const string scenePath = "Assets/EgoCapture/EgoCapture.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var rig = new GameObject("EgoCaptureXRRoot");
        var cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetParent(rig.transform, false);
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0, 0, 0, 0);
        camera.stereoTargetEye = StereoTargetEyeMask.Both;
        cameraObject.AddComponent<AudioListener>();
        cameraObject.AddComponent<EgoHeadPoseDriver>();
        rig.AddComponent<PXR_Manager>();
        rig.AddComponent<EgoCaptureRecorder>();
        Directory.CreateDirectory("Assets/EgoCapture");
        EditorSceneManager.SaveScene(scene, scenePath);

        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "org.local.picoegotracker");
        PlayerSettings.productName = "PICO EgoTracker";
        PlayerSettings.bundleVersion = "1.0";
        PlayerSettings.Android.bundleVersionCode = 20;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.allowUnsafeCode = true;

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string output = Path.GetFullPath(Path.Combine(projectRoot, "..", "Builds", "PICOEgoTrackerFusion.apk"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { scenePath },
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Android build failed: " + report.summary.result);
    }
}
