using System;
using System.IO;
using UnityEditor.Android;

// Runs after PICO's manifest writer (order 10000), preserving its XR metadata.
public sealed class EgoCaptureAndroidManifest : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 10001;

    public void OnPostGenerateGradleAndroidProject(string projectPath)
    {
        string path = Path.Combine(projectPath, "src/main/AndroidManifest.xml");
        string manifest = File.ReadAllText(path);
        const string original = "android:name=\"com.unity3d.player.UnityPlayerActivity\"";
        const string replacement = "android:name=\"org.local.picoegotracker.EgoCaptureActivity\"";
        if (!manifest.Contains(replacement))
        {
            if (!manifest.Contains(original))
                throw new Exception("UnityPlayerActivity missing from Android manifest");
            manifest = manifest.Replace(original, replacement);
        }
        const string recoveryActivity =
            "<activity android:name=\"org.local.picoegotracker.EgoRecoveryActivity\" " +
            "android:process=\":recovery\" " +
            "android:taskAffinity=\"org.local.picoegotracker.recovery\" " +
            "android:excludeFromRecents=\"true\" " +
            "android:exported=\"false\" " +
            "android:theme=\"@android:style/Theme.Translucent.NoTitleBar\" />";
        if (!manifest.Contains("org.local.picoegotracker.EgoRecoveryActivity"))
            manifest = manifest.Replace("</application>", recoveryActivity + "</application>");
        if (!manifest.Contains("android:name=\"pico_advance_interface\""))
            manifest = manifest.Replace("</application>",
                "<meta-data android:name=\"pico_advance_interface\" android:value=\"0\" />" + "</application>");
        File.WriteAllText(path, manifest);
    }
}
