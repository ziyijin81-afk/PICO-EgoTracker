using System;
using NatSuite.Recorders.Internal;

// NatCorder's stock MP4Recorder writes to Movies. This wrapper keeps the video
// beside poses.txt and tracker.txt in one app-owned session directory.
public sealed class EgoMp4Recorder : NativeRecorder
{
    public EgoMp4Recorder(string path, int width, int height, float fps, int bitrate)
        : base(Create(path, width, height, fps, bitrate)) { }

    private static IntPtr Create(string path, int width, int height, float fps, int bitrate)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("Video dimensions must be even.");
        NatCorder.CreateMP4Recorder(path, width, height, fps, 0, 0, bitrate, 2, 0,
                                     out var recorder);
        if (recorder == IntPtr.Zero)
            throw new InvalidOperationException("Could not create Android MP4 encoder.");
        return recorder;
    }
}
