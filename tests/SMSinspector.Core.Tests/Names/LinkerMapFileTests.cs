using SMSinspector.Core.Names;

namespace SMSinspector.Core.Tests.Names;

// Map lines in the CodeWarrior layout, with invented names, sizes and addresses.
public class LinkerMapFileTests
{
    private const string Map = """
        Link map of __start
         1] __start (func,global) found in boot.o

        .text section layout
          Starting        Virtual
          address  Size   address
          -----------------------
          00000000 000040 80003100  4 .text 	fooactor.o
          00000000 000008 80003100  4 getSpeed__9TFooActorCFv 	fooactor.o
          UNUSED   000008 ........ getTimer__9TFooActorCFv fooactor.o
          00000008 000038 80003108 00000108  4 update__9TFooActorFv 	fooactor.o

        .data section layout
          Starting        Virtual
          address  Size   address
          -----------------------
          00000000 000010 80200000  4 sTable 	fooactor.o
        """;

    [Fact]
    public void Reads_used_and_unused_symbols_by_section()
    {
        var symbols = LinkerMapFile.Parse(Map.Split('\n'));

        Assert.Equal(4, symbols.Count);
        var getter = symbols[0];
        Assert.Equal("getSpeed__9TFooActorCFv", getter.Name);
        Assert.Equal(".text", getter.Section);
        Assert.Equal(8u, getter.Size);
        Assert.Equal(0x80003100u, getter.Address);

        var unused = symbols[1];
        Assert.True(unused.IsUnused);
        Assert.Equal("getTimer__9TFooActorCFv", unused.Name);

        Assert.Equal(0x80003108u, symbols[2].Address);
        Assert.Equal(".data", symbols[3].Section);
    }

    [Fact]
    public void Object_file_section_labels_are_skipped()
    {
        Assert.DoesNotContain(LinkerMapFile.Parse(Map.Split('\n')), s => s.Name == ".text");
    }
}
