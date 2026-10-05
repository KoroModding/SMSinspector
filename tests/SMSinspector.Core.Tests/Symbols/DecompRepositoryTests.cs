using SMSinspector.Core.Memory;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Tests.Symbols;

// Builds a throwaway folder shaped like a decomp clone. File names and contents are invented.
public sealed class DecompRepositoryTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smsinspector-tests", Guid.NewGuid().ToString("N"));

    public DecompRepositoryTests()
    {
        File.WriteAllText(Write("configure.py"), "");
        File.WriteAllText(Write("config/GMSP01/symbols.txt"), "");
        Directory.CreateDirectory(Path.Combine(_root, ".git", "refs", "heads"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Opens_a_clone_with_configure_and_version_config()
    {
        Assert.True(DecompRepository.TryOpen(_root, GameVersion.GMSP01, out var repository, out _));
        Assert.Equal(Path.Combine(_root, "config", "GMSP01", "symbols.txt"), repository.SymbolsPath(GameVersion.GMSP01));
    }

    [Fact]
    public void Refuses_a_folder_without_configure_py()
    {
        File.Delete(Path.Combine(_root, "configure.py"));

        Assert.False(DecompRepository.TryOpen(_root, GameVersion.GMSP01, out _, out var error));
        Assert.Contains("configure.py", error);
    }

    [Fact]
    public void Refuses_a_clone_without_config_for_the_version()
    {
        Assert.False(DecompRepository.TryOpen(_root, GameVersion.GMSJ01, out _, out var error));
        Assert.Contains("config/GMSJ01/", error);
    }

    [Fact]
    public void Refuses_a_config_folder_without_symbols()
    {
        File.Delete(Path.Combine(_root, "config", "GMSP01", "symbols.txt"));

        Assert.False(DecompRepository.TryOpen(_root, GameVersion.GMSP01, out _, out var error));
        Assert.Contains("symbols.txt", error);
    }

    [Fact]
    public void Linker_map_comes_from_config_yml_even_when_commented()
    {
        File.WriteAllText(Write("config/GMSP01/config.yml"), "name: test\n# map: orig/GMSP01/files/first.MAP\n");
        File.WriteAllText(Write("orig/GMSP01/files/first.MAP"), "");
        File.WriteAllText(Write("orig/GMSP01/files/second.MAP"), "");

        var lookup = Open().LocateLinkerMap(GameVersion.GMSP01);

        Assert.Equal(Path.Combine(_root, "orig/GMSP01/files/first.MAP"), lookup.Path);
        Assert.Equal("orig/GMSP01/files/first.MAP", lookup.ConfiguredPath);
    }

    [Fact]
    public void Missing_configured_map_falls_back_to_the_only_map_present()
    {
        File.WriteAllText(Write("config/GMSP01/config.yml"), "map: orig/GMSP01/files/named.MAP\n");
        File.WriteAllText(Write("orig/GMSP01/files/actual.map"), "");

        var lookup = Open().LocateLinkerMap(GameVersion.GMSP01);

        Assert.Equal("actual.map", Path.GetFileName(lookup.Path));
        Assert.Contains("named.MAP, which is not there", lookup.Message);
    }

    [Fact]
    public void Several_maps_and_no_usable_config_choose_nothing_and_say_why()
    {
        File.WriteAllText(Write("orig/GMSP01/files/a.MAP"), "");
        File.WriteAllText(Write("orig/GMSP01/files/b.MAP"), "");

        var lookup = Open().LocateLinkerMap(GameVersion.GMSP01);

        Assert.Null(lookup.Path);
        Assert.Equal(2, lookup.Found.Count);
        Assert.Contains("a.MAP, b.MAP", lookup.Message);
    }

    [Fact]
    public void No_map_at_all_is_reported()
    {
        var lookup = Open().LocateLinkerMap(GameVersion.GMSP01);

        Assert.Null(lookup.Path);
        Assert.Empty(lookup.Found);
        Assert.Contains("no .MAP file", lookup.Message);
    }

    [Fact]
    public void Reads_the_commit_from_a_loose_ref()
    {
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(_root, ".git", "refs", "heads", "main"), Hash + "\n");

        Assert.Equal(Hash, Open().ReadCommitHash());
    }

    [Fact]
    public void Reads_the_commit_from_packed_refs()
    {
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(_root, ".git", "packed-refs"), $"# pack-refs with: peeled\n{Hash} refs/heads/main\n");

        Assert.Equal(Hash, Open().ReadCommitHash());
    }

    [Fact]
    public void Reads_a_detached_head_and_tolerates_no_git()
    {
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), Hash);
        Assert.Equal(Hash, Open().ReadCommitHash());

        Directory.Delete(Path.Combine(_root, ".git"), recursive: true);
        Assert.Null(Open().ReadCommitHash());
    }

    private string Write(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private DecompRepository Open()
    {
        Assert.True(DecompRepository.TryOpen(_root, GameVersion.GMSP01, out var repository, out _));
        return repository;
    }
}
