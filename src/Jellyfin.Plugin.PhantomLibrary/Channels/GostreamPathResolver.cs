using System;
using System.IO;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

internal static class GostreamPathResolver
{
    public static string ResolveMoviePath(string path)
        => ResolvePath(path, Plugin.Instance?.Configuration.GostreamMoviesRoot);

    public static string ResolveEpisodePath(string path)
        => ResolvePath(path, Plugin.Instance?.Configuration.GostreamShowsRoot);

    internal static string ResolvePath(string path, string? configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path) || string.IsNullOrWhiteSpace(configuredRoot) || !Directory.Exists(configuredRoot))
        {
            return path;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return path;
        }

        var direct = Path.Combine(configuredRoot, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(configuredRoot, fileName, SearchOption.AllDirectories))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            return path;
        }

        return path;
    }
}
