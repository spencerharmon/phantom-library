using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.PhantomLibrary.Playback;

/// <summary>
/// Lazily extracts the embedded splash.mp4 into the Jellyfin cache
/// directory so it can be served as a regular local file via
/// <c>MediaProtocol.File</c>. Re-extracts when the assembly version
/// changes (tracked in a sibling <c>splash.version</c> file).
/// </summary>
public static class SplashStream
{
    internal const string ResourceName = "Jellyfin.Plugin.PhantomLibrary.Assets.splash.mp4";
    private const string CacheSubdir = "PhantomLibrary";
    private const string SplashFileName = "splash.mp4";
    private const string VersionFileName = "splash.version";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string GetLocalPath(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return GetLocalPathAsync(applicationPaths, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task<string> GetLocalPathAsync(IApplicationPaths applicationPaths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        var dir = Path.Combine(applicationPaths.CachePath, CacheSubdir);
        var splashPath = Path.Combine(dir, SplashFileName);
        var versionPath = Path.Combine(dir, VersionFileName);
        var currentVersion = ResolveAssemblyVersion();

        if (IsFresh(splashPath, versionPath, currentVersion))
        {
            return splashPath;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(splashPath, versionPath, currentVersion))
            {
                return splashPath;
            }

            Directory.CreateDirectory(dir);
            await ExtractAsync(splashPath, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(versionPath, currentVersion, ct).ConfigureAwait(false);
            return splashPath;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsFresh(string splashPath, string versionPath, string currentVersion)
    {
        if (!File.Exists(splashPath) || !File.Exists(versionPath))
        {
            return false;
        }

        try
        {
            var stored = File.ReadAllText(versionPath).Trim();
            return string.Equals(stored, currentVersion, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static string ResolveAssemblyVersion()
    {
        var asm = typeof(SplashStream).Assembly;
        return asm.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    private static async Task ExtractAsync(string destination, CancellationToken ct)
    {
        var asm = typeof(SplashStream).Assembly;
        using var src = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded splash resource '{ResourceName}' not found in assembly. " +
                "Did the build target produce Assets/splash.mp4?");

        var tmp = destination + ".tmp";
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        // Atomic-ish replace; on Windows File.Move with overwrite is supported on net6+.
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(tmp, destination);
    }
}
