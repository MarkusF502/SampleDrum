using System.Reflection;
using System.Security.Cryptography;

namespace SampleDrum.Remote;

/// <summary>The phone page (wwwroot/**), embedded in the assembly and served from memory.</summary>
internal static class StaticAssets
{
    public sealed record Asset(byte[] Content, string ContentType, string ETag);

    private const string Prefix = "wwwroot/";

    private static readonly Lazy<IReadOnlyDictionary<string, Asset>> Files = new(Load);

    /// <summary>Asset by path relative to wwwroot with '/' (case-sensitive), or null.</summary>
    public static Asset? Get(string relativePath) =>
        Files.Value.TryGetValue(relativePath, out var a) ? a : null;

    public static IEnumerable<string> Paths => Files.Value.Keys;

    private static IReadOnlyDictionary<string, Asset> Load()
    {
        var asm = typeof(StaticAssets).Assembly;
        var map = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            string path = name.Replace('\\', '/');
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            path = path[Prefix.Length..];
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            string etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + "\"";
            map[path] = new Asset(bytes, ContentTypeOf(path), etag);
        }
        return map;
    }

    internal static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".webmanifest" => "application/manifest+json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
