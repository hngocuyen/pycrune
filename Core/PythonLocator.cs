namespace PycRune.Core;

public static class PythonLocator
{

    public static readonly IReadOnlyDictionary<int, string> MagicVersions = new Dictionary<int, string>
    {
        [3413] = "3.8",
        [3425] = "3.9",
        [3439] = "3.10",
        [3495] = "3.11",
        [3531] = "3.12",
        [3571] = "3.13",
        [3627] = "3.14",
        [3666] = "3.15",
    };

    public static (string FileName, string VersionArg, string? Version) Resolve(string path, string? versionHint = null)
    {
        if (!path.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
        {


            return ResolveVersion(versionHint);
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            var read = stream.Read(header);
            if (read < 2)
            {
                return ("python", "", null);
            }

            var magic = header[0] | (header[1] << 8);
            if (!MagicVersions.TryGetValue(magic, out var version))
            {
                return ("python", "", null);
            }

            if (version == "3.11")
            {
                var embedded = FindEmbedded311();
                if (embedded != null)
                {
                    return (embedded, "", version);
                }
            }


            if (version == "3.12")
            {
                return ("python", "", version);
            }

            return ("py", $"-{version}", version);
        }
        catch
        {
            return ("python", "", null);
        }
    }

    public static (string FileName, string VersionArg, string? Version) ResolveVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return ("python", "", null);

        var parts = version.Split('.');
        if (parts.Length < 2) return ("python", "", null);
        var majorMinor = $"{parts[0]}.{parts[1]}";

        if (majorMinor == "3.11")
        {
            var embedded = FindEmbedded311();
            if (embedded != null)
            {
                return (embedded, "", majorMinor);
            }
        }

        if (majorMinor == "3.12")
        {
            return ("python", "", majorMinor);
        }

        return ("py", $"-{majorMinor}", majorMinor);
    }

    private static string? FindEmbedded311()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "multiver", "python311-embed", "python.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "multiver", "python311-embed", "python.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
