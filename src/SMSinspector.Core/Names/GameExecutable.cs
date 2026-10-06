using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Names;

public enum ExecutableStatus
{
    Verified,
    Missing,
    NoExpectedHash,
    HashMismatch,
    Unreadable,
}

/// <param name="Image">The parsed executable; only set when the hash was verified.</param>
/// <param name="Path">Where the executable was looked for.</param>
public sealed record ExecutableLookup(ExecutableStatus Status, DolImage? Image, string Path, string? Sha1, string? ExpectedSha1, string Message)
{
    public bool IsUsable => Status == ExecutableStatus.Verified && Image is not null;
}

/// <summary>
/// The user's own <c>main.dol</c>, read where the decomp expects it. It is only used when
/// its SHA-1 matches the <c>.dol</c> line of <c>config/&lt;version&gt;/build.sha1</c>: a matching
/// build is byte-identical to the original executable, so that line is the original's hash.
/// </summary>
public static partial class GameExecutable
{
    public static ExecutableLookup Locate(DecompRepository repository, GameVersion version)
    {
        var relative = ConfiguredPath(repository, version);
        var path = Path.GetFullPath(Path.Combine(repository.Root, relative));
        var display = relative.Replace('\\', '/');

        if (!File.Exists(path))
        {
            return new ExecutableLookup(ExecutableStatus.Missing, null, path, null, null,
                $"No executable at {display}: extract the system data of your disc there to enable the dol accessor source.");
        }

        var expected = ExpectedHash(repository, version);
        if (expected is null)
        {
            return new ExecutableLookup(ExecutableStatus.NoExpectedHash, null, path, null, null,
                $"config/{version}/build.sha1 has no .dol line, so {display} cannot be verified and is not used.");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ExecutableLookup(ExecutableStatus.Unreadable, null, path, null, expected, $"Could not read {display}: {e.Message}");
        }

        return Verify(bytes, expected, path, display);
    }

    /// <summary>Checks the hash, then parses. Split out so tests can run it on bytes built in memory.</summary>
    public static ExecutableLookup Verify(byte[] bytes, string expectedSha1, string path, string display)
    {
        var actual = Convert.ToHexStringLower(SHA1.HashData(bytes));
        if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutableLookup(ExecutableStatus.HashMismatch, null, path, actual, expectedSha1,
                $"{display} has SHA-1 {actual}, the decomp expects {expectedSha1.ToLowerInvariant()}: not the original executable, so it is not used.");
        }

        if (!DolImage.TryParse(bytes, out var image, out var error))
        {
            return new ExecutableLookup(ExecutableStatus.Unreadable, null, path, actual, expectedSha1, $"{display} could not be parsed: {error}");
        }

        return new ExecutableLookup(ExecutableStatus.Verified, image, path, actual, expectedSha1, $"Using {display} (SHA-1 verified).");
    }

    /// <summary>
    /// <c>object_base</c> joined with <c>object</c> from config.yml, which the decomp's build
    /// reads too; <c>orig/&lt;version&gt;/sys/main.dol</c> when config.yml does not say.
    /// </summary>
    public static string ConfiguredPath(DecompRepository repository, GameVersion version)
    {
        string? objectBase = null;
        string? objectPath = null;
        var configPath = Path.Combine(repository.ConfigDirectory(version), "config.yml");
        if (File.Exists(configPath))
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var match = TopLevelKey().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var value = match.Groups["value"].Value.Trim('"', '\'');
                switch (match.Groups["key"].Value)
                {
                    case "object_base":
                        objectBase ??= value;
                        break;
                    case "object":
                        objectPath ??= value;
                        break;
                }
            }
        }

        if (objectPath is null)
        {
            return Path.Combine("orig", version.ToString(), "sys", "main.dol");
        }

        return objectBase is null ? objectPath : Path.Combine(objectBase, objectPath);
    }

    /// <summary>The hash on the line of build.sha1 that names a .dol file.</summary>
    public static string? ExpectedHash(DecompRepository repository, GameVersion version)
    {
        var path = Path.Combine(repository.ConfigDirectory(version), "build.sha1");
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var match = Sha1Line().Match(line);
            if (match.Success && match.Groups["file"].Value.EndsWith(".dol", StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(?<key>object_base|object):\s*(?<value>\S+)\s*(?:#.*)?$")]
    private static partial Regex TopLevelKey();

    [GeneratedRegex(@"^(?<hash>[0-9A-Fa-f]{40})\s+\*?(?<file>\S+)\s*$")]
    private static partial Regex Sha1Line();
}
