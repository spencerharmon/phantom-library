using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

internal static class MediaSourceIds
{
    public static string ForSplashPath(string path) => GuidFromPath("splash", path);

    public static string ForFilePath(string path) => GuidFromPath("file", path);

    private static string GuidFromPath(string kind, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + path));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes).ToString("D", CultureInfo.InvariantCulture);
    }
}
