using System.Security.Cryptography;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Names;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Names;

// A throwaway folder shaped like a decomp clone, with a DOL built in memory.
public sealed class GameExecutableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smsinspector-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _dol = TestDol.Build(0x80003000, TestDol.Lwz(3, 0x10), TestDol.Blr);

    public GameExecutableTests()
    {
        Write("configure.py", "");
        Write("config/GMSP01/symbols.txt", "");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Dol_reads_words_inside_its_sections_only()
    {
        Assert.True(DolImage.TryParse(_dol, out var image, out _));

        Assert.True(image.TryReadWord(0x80003004, out var word));
        Assert.Equal(TestDol.Blr, word);
        Assert.False(image.TryReadWord(0x80003008, out _));
        Assert.False(image.TryReadWord(0x80002FFC, out _));
    }

    [Fact]
    public void Dol_with_a_section_past_the_end_is_rejected()
    {
        var truncated = _dol[..0x104];

        Assert.False(DolImage.TryParse(truncated, out _, out var error));
        Assert.Contains("past the end", error);
    }

    [Fact]
    public void Executable_with_the_expected_hash_is_used()
    {
        WriteSha1(Hash(_dol));
        WriteBytes("orig/GMSP01/sys/main.dol", _dol);

        var lookup = GameExecutable.Locate(Open(), GameVersion.GMSP01);

        Assert.Equal(ExecutableStatus.Verified, lookup.Status);
        Assert.True(lookup.IsUsable);
        Assert.Contains("SHA-1 verified", lookup.Message);
    }

    [Fact]
    public void Executable_with_another_hash_is_refused()
    {
        WriteSha1(new string('0', 40));
        WriteBytes("orig/GMSP01/sys/main.dol", _dol);

        var lookup = GameExecutable.Locate(Open(), GameVersion.GMSP01);

        Assert.Equal(ExecutableStatus.HashMismatch, lookup.Status);
        Assert.False(lookup.IsUsable);
        Assert.Null(lookup.Image);
        Assert.Contains(Hash(_dol), lookup.Message);
        Assert.Contains("not used", lookup.Message);
    }

    [Fact]
    public void Missing_executable_is_reported()
    {
        WriteSha1(Hash(_dol));

        var lookup = GameExecutable.Locate(Open(), GameVersion.GMSP01);

        Assert.Equal(ExecutableStatus.Missing, lookup.Status);
        Assert.Contains("orig/GMSP01/sys/main.dol", lookup.Message);
    }

    [Fact]
    public void Executable_without_an_expected_hash_is_not_used()
    {
        Write("config/GMSP01/build.sha1", "0123456789abcdef0123456789abcdef01234567  build/GMSP01/other.rel\n");
        WriteBytes("orig/GMSP01/sys/main.dol", _dol);

        var lookup = GameExecutable.Locate(Open(), GameVersion.GMSP01);

        Assert.Equal(ExecutableStatus.NoExpectedHash, lookup.Status);
        Assert.False(lookup.IsUsable);
    }

    [Fact]
    public void Path_comes_from_config_yml()
    {
        Write("config/GMSP01/config.yml", "name: test\nobject_base: orig/elsewhere\nobject: boot/game.dol\n");
        WriteSha1(Hash(_dol));
        WriteBytes("orig/elsewhere/boot/game.dol", _dol);

        var lookup = GameExecutable.Locate(Open(), GameVersion.GMSP01);

        Assert.Equal(ExecutableStatus.Verified, lookup.Status);
        Assert.EndsWith(Path.Combine("boot", "game.dol"), lookup.Path);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    private void WriteSha1(string hash) => Write("config/GMSP01/build.sha1", $"{hash}  build/GMSP01/test.dol\n");

    private DecompRepository Open()
    {
        Assert.True(DecompRepository.TryOpen(_root, GameVersion.GMSP01, out var repository, out _));
        return repository;
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteBytes(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
