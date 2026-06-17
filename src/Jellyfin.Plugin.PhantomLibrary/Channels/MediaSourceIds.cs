using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

internal static class MediaSourceIds
{
    public static string ForSplashPath(string path) => "phantom-splash:" + Hash(path);

    public static string ForFilePath(string path) => "phantom-file:" + Hash(path);

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
