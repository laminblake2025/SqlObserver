using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlObserver.Server;

/// <summary>Loads and serves a bounded, immutable snapshot of the reviewed web build.</summary>
internal sealed partial class WebInterfaceAssetCatalog
{
    internal const string ConfigurationKey = "SqlObserver:WebRootPath";
    private const long MaximumAssetBytes = 8 * 1024 * 1024;
    private const long MaximumCatalogBytes = 64 * 1024 * 1024;
    private const int MaximumAssets = 256;
    private const int MaximumFileSystemEntries = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ReadOnlyDictionary<string, string> ContentTypes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".avif"] = "image/avif",
            [".css"] = "text/css; charset=utf-8",
            [".eot"] = "application/vnd.ms-fontobject",
            [".gif"] = "image/gif",
            [".ico"] = "image/x-icon",
            [".jpeg"] = "image/jpeg",
            [".jpg"] = "image/jpeg",
            [".js"] = "text/javascript; charset=utf-8",
            [".mp3"] = "audio/mpeg",
            [".mp4"] = "video/mp4",
            [".ogg"] = "audio/ogg",
            [".otf"] = "font/otf",
            [".png"] = "image/png",
            [".svg"] = "image/svg+xml",
            [".ttf"] = "font/ttf",
            [".wav"] = "audio/wav",
            [".webm"] = "video/webm",
            [".webp"] = "image/webp",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
        });

    private readonly ReadOnlyDictionary<string, WebInterfaceAsset> _assets;

    private WebInterfaceAssetCatalog(byte[] index, Dictionary<string, WebInterfaceAsset> assets)
    {
        Index = index;
        _assets = new ReadOnlyDictionary<string, WebInterfaceAsset>(assets);
    }

    internal byte[] Index { get; }

    internal static WebInterfaceAssetCatalog? Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? configuredPath = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException($"{ConfigurationKey} must be an absolute path.");
        }

        string root = Path.GetFullPath(configuredPath);
        AssertSafeAncestors(root);
        AssertSafeDirectory(root, "web root");
        string assetsRoot = Path.Combine(root, "assets");
        AssertSafeDirectory(assetsRoot, "web assets directory");

        byte[] index = ReadStableFile(Path.Combine(root, "index.html"), 1024 * 1024, "web index");
        string indexText;
        try
        {
            indexText = StrictUtf8.GetString(index);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("The web index is not valid UTF-8.", exception);
        }

        if (!indexText.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) ||
            indexText.Contains('\r') ||
            indexText.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
            !IndexAssetReferenceRegex().IsMatch(indexText))
        {
            throw new InvalidOperationException("The web index does not have the expected bounded shape.");
        }

        var assets = new Dictionary<string, WebInterfaceAsset>(StringComparer.Ordinal);
        long totalBytes = index.Length;
        foreach (string path in EnumerateSafeAssetFiles(assetsRoot))
        {
            if (assets.Count >= MaximumAssets)
            {
                throw new InvalidOperationException($"The web build contains more than {MaximumAssets} assets.");
            }

            string relative = Path.GetRelativePath(assetsRoot, path).Replace('\\', '/');
            if (!IsSafeRelativeAssetPath(relative))
            {
                throw new InvalidOperationException($"The web build contains an unsafe asset path: {relative}");
            }

            string extension = Path.GetExtension(relative).ToLowerInvariant();
            if (extension == ".map")
            {
                continue;
            }

            if (!ContentTypes.TryGetValue(extension, out string? contentType))
            {
                throw new InvalidOperationException($"The web build contains an unsupported asset type: {relative}");
            }

            if (!HashedAssetNameRegex().IsMatch(Path.GetFileName(relative)))
            {
                throw new InvalidOperationException($"The web build contains a non-content-hashed asset: {relative}");
            }

            byte[] bytes = ReadStableFile(path, MaximumAssetBytes, $"web asset {relative}");
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > MaximumCatalogBytes)
            {
                throw new InvalidOperationException($"The web build exceeds {MaximumCatalogBytes} bytes.");
            }

            assets.Add(relative, new WebInterfaceAsset(bytes, contentType));
        }

        if (assets.Count == 0)
        {
            throw new InvalidOperationException("The web build contains no assets.");
        }

        MatchCollection references = IndexAssetReferenceRegex().Matches(indexText);
        if (references.Count == 0)
        {
            throw new InvalidOperationException("The web index references no bounded assets.");
        }

        foreach (Match match in references)
        {
            string referenced = match.Groups[1].Value;
            if (!assets.ContainsKey(referenced))
            {
                throw new InvalidOperationException($"The web index references a missing asset: {referenced}");
            }
        }

        return new WebInterfaceAssetCatalog(index, assets);
    }

    internal bool TryGetAsset(string? requestedPath, out WebInterfaceAsset? asset)
    {
        if (requestedPath is null || !IsSafeRelativeAssetPath(requestedPath))
        {
            asset = null;
            return false;
        }

        return _assets.TryGetValue(requestedPath, out asset);
    }

    private static void AssertSafeDirectory(string path, string label)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists ||
            (directory.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            throw new InvalidOperationException($"The {label} is missing or unsafe.");
        }
    }

    private static void AssertSafeAncestors(string path)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The web root has a missing or unsafe ancestor.");
            }

            current = current.Parent;
        }
    }

    private static List<string> EnumerateSafeAssetFiles(string root)
    {
        var directories = new Stack<string>();
        var files = new List<string>();
        directories.Push(root);
        int entryCount = 0;
        while (directories.Count > 0)
        {
            string current = directories.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
            {
                entryCount++;
                if (entryCount > MaximumFileSystemEntries)
                {
                    throw new InvalidOperationException($"The web assets tree contains more than {MaximumFileSystemEntries} entries.");
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    throw new InvalidOperationException("The web assets tree contains an unsafe entry.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static byte[] ReadStableFile(string path, long maximumBytes, string label)
    {
        var before = new FileInfo(path);
        if (!before.Exists ||
            (before.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0 ||
            before.Length < 1 ||
            before.Length > maximumBytes)
        {
            throw new InvalidOperationException($"The {label} is missing, empty, oversized, or unsafe.");
        }

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length != before.Length || stream.Length > int.MaxValue)
            {
                throw new InvalidOperationException($"The {label} changed while it was opened.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        var after = new FileInfo(path);
        if (!after.Exists || after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            throw new InvalidOperationException($"The {label} changed while it was read.");
        }

        return bytes;
    }

    private static bool IsSafeRelativeAssetPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\\'))
        {
            return false;
        }

        string[] segments = value.Split('/');
        return segments.Length <= 8 && segments.All(static segment => SafeAssetSegmentRegex().IsMatch(segment));
    }

    [GeneratedRegex("(?:src|href)=\"/assets/([A-Za-z0-9._/-]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex IndexAssetReferenceRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAssetSegmentRegex();

    [GeneratedRegex("(?:^|[-_.])[A-Za-z0-9_-]{8}(?=[-_.]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex HashedAssetNameRegex();
}

internal sealed record WebInterfaceAsset(byte[] Bytes, string ContentType);

internal static class WebInterfaceEndpoints
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; base-uri 'none'; connect-src 'self'; font-src 'self'; " +
        "form-action 'self'; frame-ancestors 'none'; img-src 'self' data:; script-src 'self'; style-src 'self'";

    internal static IEndpointRouteBuilder MapSqlObserverWebInterface(
        this IEndpointRouteBuilder endpoints,
        WebInterfaceAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(catalog);

        endpoints.MapGet("/", (HttpContext context) =>
        {
            SetSecurityHeaders(context, "no-store");
            return Results.Bytes(catalog.Index, "text/html; charset=utf-8");
        }).RequireAuthorization();

        endpoints.MapGet("/assets/{**assetPath}", (HttpContext context, string? assetPath) =>
        {
            if (!catalog.TryGetAsset(assetPath, out WebInterfaceAsset? asset) || asset is null)
            {
                return Results.NotFound();
            }

            SetSecurityHeaders(context, "public, max-age=31536000, immutable");
            return Results.Bytes(asset.Bytes, asset.ContentType);
        }).RequireAuthorization();

        return endpoints;
    }

    private static void SetSecurityHeaders(HttpContext context, string cacheControl)
    {
        context.Response.Headers.CacheControl = cacheControl;
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
    }
}
