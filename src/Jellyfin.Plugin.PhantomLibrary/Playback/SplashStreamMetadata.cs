namespace Jellyfin.Plugin.PhantomLibrary.Playback;

/// <summary>
/// Pre-baked metadata for the splash MP4. Values pulled from
/// <c>ffprobe -v error -show_streams -of json Assets/splash.mp4</c>
/// of the build-time generated file (1280x720, 30 fps, H.264 High@4.0,
/// CRF 22, AAC-LC stereo 48 kHz @ 64 kbps target, ~10 s).
/// </summary>
internal static class SplashStreamMetadata
{
    public const int Width = 1280;
    public const int Height = 720;
    public const int VideoFps = 30;
    public const string VideoCodec = "h264";
    public const string VideoProfile = "High";
    public const int VideoLevel = 40;            // ffprobe reports as integer (level * 10)
    public const string PixelFormat = "yuv420p";
    public const int VideoBitRate = 71040;       // bits/s, from ffprobe
    public const string AudioCodec = "aac";
    public const string AudioProfile = "LC";
    public const int AudioSampleRate = 48000;
    public const int AudioChannels = 2;
    public const string AudioChannelLayout = "stereo";
    public const int AudioBitRate = 64000;       // target; ffprobe reports actual ~2277 for silence
    public const string Container = "mp4";
    public const double DurationSeconds = 10.0;
}
