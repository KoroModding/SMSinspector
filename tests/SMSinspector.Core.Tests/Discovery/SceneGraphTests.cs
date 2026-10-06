using System.Buffers.Binary;
using System.Text;
using SMSinspector.Core.Discovery;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Symbols;
using SMSinspector.Core.Tests.Fakes;

namespace SMSinspector.Core.Tests.Discovery;

// A small invented world: anchor classes shaped like the decomp's, invented game classes,
// and objects placed in fake memory at offsets read from the computed layouts.
public class SceneGraphTests
{
    private const string Headers = """
        namespace JDrama {
        class TNameRef {
        public:
            virtual ~TNameRef();
            const char* mName;
            u16 mKeyCode;
        };

        class TNameRefGen {
        public:
            virtual void load();
            TNameRef* mRootNameRef;
        };
        }

        namespace JGadget {
        template <typename T> class TAllocator { };

        template <class T, class A = TAllocator<T> > class TList {
        public:
            struct TNode_ {
                TNode_* pNext_;
                TNode_* pPrev_;
            };

            A mAllocator;
            u32 mSize;
            TNode_ oEnd_;
        };

        class TList_pointer_void : public TList<void*> { };

        template <class T> class TList_pointer : TList_pointer_void { };
        }

        class TGroup : public JDrama::TNameRef, public JGadget::TList_pointer<JDrama::TNameRef*> { };

        class TActor : public JDrama::TNameRef {
        public:
            TActor* mPartner;
            TGroup* mGroups[2];
            TActor** mMany;
        };

        class TSubActor : public TActor { };

        class TOther : public JDrama::TNameRef { };
        """;

    [Fact]
    public void Walks_lists_and_member_pointers_from_the_anchors()
    {
        var world = new World();
        var a = world.New("TActor", "first");
        var b = world.New("TSubActor", "second");
        var c = world.New("TActor", "third");
        world.List(world.Root, a, b);
        world.Set(a, "mGroups", world.New("TGroup", "group"), 0);
        world.Set(b, "mPartner", c);

        var graph = world.Walk();

        Assert.Null(graph.Error);
        Assert.Equal(5, graph.Nodes.Count);
        Assert.Equal(EdgeKind.Root, graph.Nodes[0].Edge);

        var second = graph.Nodes.Single(n => n.Address == b);
        Assert.Equal(EdgeKind.List, second.Edge);
        Assert.Equal(1, second.Index);
        Assert.Equal("TSubActor", second.ClassName.Value);
        Assert.Equal(ProvenanceKind.Symbol, second.ClassName.Source.Kind);
        Assert.Equal("second", second.InstanceName?.Value);
        Assert.Equal(ProvenanceKind.Memory, second.InstanceName?.Source.Kind);

        var third = graph.Nodes.Single(n => n.Address == c);
        Assert.Equal(EdgeKind.Member, third.Edge);
        Assert.Equal(b, third.Parent);
        Assert.Equal("mPartner", third.Via?.Value);
        Assert.Equal(ProvenanceKind.Header, third.Via?.Source.Kind);
    }

    [Fact]
    public void Cycles_do_not_loop()
    {
        var world = new World();
        var a = world.New("TActor", "a");
        var b = world.New("TActor", "b");
        world.List(world.Root, a);
        world.Set(a, "mPartner", b);
        world.Set(b, "mPartner", a);
        world.Set(b, "mGroups", world.Root, 0);

        var graph = world.Walk();

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(graph.Nodes.Count, graph.Nodes.Select(n => n.Address).Distinct().Count());
        Assert.Null(graph.Truncated);
    }

    [Fact]
    public void Bad_pointers_are_rejected_and_counted_not_followed()
    {
        var world = new World();
        var a = world.New("TActor", "a");
        var b = world.New("TActor", "b");
        var other = world.New("TOther", "other");
        var ghost = world.New("TGhost", "ghost");
        var blank = world.Alloc(0x20);
        world.List(world.Root, a, b);
        world.Set(a, "mPartner", 0x90000000);
        world.Set(a, "mGroups", world.Root + 2, 0);
        world.Set(a, "mGroups", blank, 1);
        world.Set(b, "mPartner", other);
        world.List(world.Root, a, b, ghost);

        var graph = world.Walk();

        Assert.Equal(1, graph.Rejected[RejectReason.OutsideMem1]);
        Assert.Equal(1, graph.Rejected[RejectReason.Misaligned]);
        Assert.Equal(1, graph.Rejected[RejectReason.UnknownVtable]);
        Assert.Equal(1, graph.Rejected[RejectReason.WrongClass]);
        Assert.Equal(1, graph.Rejected[RejectReason.ClassWithoutLayout]);
        Assert.False(graph.Contains(other));
        Assert.False(graph.Contains(ghost));

        var wrong = graph.RejectedSamples.Single(s => s.Reason == RejectReason.WrongClass);
        Assert.Equal("TActor", wrong.Declared);
        Assert.Equal("TOther", wrong.Actual);
    }

    [Fact]
    public void Pointer_to_pointer_members_are_counted_not_followed()
    {
        var world = new World();
        var a = world.New("TActor", "a");
        var hidden = world.New("TActor", "hidden");
        var array = world.Alloc(8);
        world.Put(array, hidden);
        world.Set(a, "mMany", array);
        world.List(world.Root, a);

        var graph = world.Walk();

        Assert.Equal(1, graph.DoublePointerMembers);
        Assert.False(graph.Contains(hidden));
    }

    [Fact]
    public void Depth_limit_cuts_the_walk_and_says_how_far_it_got()
    {
        var world = new World();
        var chain = Enumerable.Range(0, 5).Select(i => world.New("TActor", $"link {i}")).ToArray();
        world.List(world.Root, chain[0]);
        for (var i = 0; i + 1 < chain.Length; i++)
        {
            world.Set(chain[i], "mPartner", chain[i + 1]);
        }

        var graph = world.Walk(new WalkLimits(2, TimeSpan.FromSeconds(5)));

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Contains("depth limit of 2", graph.Truncated);
        Assert.Contains("3 objects reached", graph.Truncated);
    }

    [Fact]
    public void Time_budget_cuts_the_walk()
    {
        var world = new World();
        world.List(world.Root, world.New("TActor", "a"));

        var graph = world.Walk(new WalkLimits(64, TimeSpan.Zero));

        Assert.Contains("time budget", graph.Truncated);
        Assert.Contains("0 objects reached", graph.Truncated);
    }

    [Fact]
    public void List_whose_length_disagrees_with_its_size_is_counted()
    {
        var world = new World();
        world.List(world.Root, world.New("TActor", "a"), world.New("TActor", "b"));
        world.SetListSize(world.Root, 5);

        var graph = world.Walk();

        Assert.Equal(1, graph.ListSizeMismatches);
        Assert.Equal(3, graph.Nodes.Count);
    }

    [Fact]
    public void Scanned_objects_the_walk_misses_are_marked_possibly_stale()
    {
        var world = new World();
        var live = world.New("TActor", "live");
        var stale = world.New("TActor", "");
        world.List(world.Root, live);

        var graph = world.Walk();
        var scan = world.Scan();
        var merge = DiscoveryMerge.Build(graph, scan, world.Walker().IsGraphNodeClass);

        Assert.Equal([stale], merge.NotReached.Select(o => o.Address));
        Assert.Contains(SceneGraphText.NotReachedLabel, SceneGraphText.Describe(graph, merge));
        Assert.DoesNotContain(merge.NotReached, o => o.Address == live);
    }

    [Fact]
    public void A_missing_anchor_disables_the_walk_with_a_reason()
    {
        var world = new World(Headers.Replace("TNameRef* mRootNameRef;", "TNameRef* mOtherRoot;", StringComparison.Ordinal));

        var graph = world.Walk();

        Assert.Empty(graph.Nodes);
        Assert.Contains(Anchors.SceneGraphRoot.Name, graph.Error);
        Assert.StartsWith("Scene graph not walked", SceneGraphText.Describe(graph));
    }

    /// <summary>Fake memory with objects laid out by the engine, and their vtable symbols.</summary>
    private sealed class World
    {
        private const uint Window = 0x80400000;
        private const uint VtableBase = 0x803D0000;

        private readonly byte[] _bytes = new byte[VtableScanner.ChunkSize];
        private readonly List<Symbol> _symbols = [];
        private readonly Dictionary<string, uint> _vtables = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, string> _classes = [];
        private readonly LoadedLayouts _layouts;
        private uint _next = Window + 0x100;

        public World(string headers = Headers)
        {
            var catalog = TestHeaders.Catalog(headers);
            var engine = new LayoutEngine(catalog);
            _layouts = new LoadedLayouts(catalog, engine, LayoutReport.Build(catalog, engine));

            var generator = New("JDrama::TNameRefGen", null);
            var holder = Alloc(4);
            Put(holder, generator);
            _symbols.Add(new Symbol(Anchors.SceneGraphInstance.Name, ".sbss", holder, 4, "object", "global"));
            Root = New("TGroup", "root");
            if (Find("JDrama::TNameRefGen").Flatten().Any(f => f.Field.Name == "mRootNameRef"))
            {
                Set(generator, "mRootNameRef", Root);
            }
        }

        public uint Root { get; }

        public uint Alloc(uint size)
        {
            var address = _next;
            _next += (size + 7) & ~7u;
            return address;
        }

        /// <summary>A new object; a class missing from the headers still gets a vtable symbol.</summary>
        public uint New(string className, string? name)
        {
            var layout = _layouts.Find(className, VersionMask.Pal);
            var address = Alloc(layout?.Size ?? 0x10);
            Put(address + (layout?.VptrOffset ?? 0), Vtable(className));
            _classes[address] = className;
            if (name is not null && layout is not null)
            {
                var text = Alloc((uint)Encoding.ASCII.GetByteCount(name) + 1);
                Encoding.ASCII.GetBytes(name).CopyTo(_bytes, (int)(text - Window));
                Set(address, "mName", text);
            }

            return address;
        }

        public void Set(uint address, string member, uint value, int index = 0)
        {
            var field = Find(_classes[address]).Flatten().First(f => f.Field.Name == member);
            Put(address + field.AbsoluteOffset!.Value + (uint)(index * 4), value);
        }

        /// <summary>Fills the list of a TGroup object, nodes allocated as the JGadget code does.</summary>
        public void List(uint group, params uint[] items)
        {
            var (sentinel, size, valueOffset) = ListOffsets(group);
            var previous = sentinel;
            foreach (var item in items)
            {
                var node = Alloc(valueOffset + 4);
                Put(previous, node);
                Put(node + 4, previous);
                Put(node + valueOffset, item);
                previous = node;
            }

            Put(previous, sentinel);
            Put(sentinel + 4, previous);
            Put(size, (uint)items.Length);
        }

        public void SetListSize(uint group, uint value) => Put(ListOffsets(group).Size, value);

        public void Put(uint address, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(_bytes.AsSpan((int)(address - Window)), value);

        public SceneGraphWalker Walker(WalkLimits? limits = null)
        {
            var symbols = new SymbolTable(_symbols);
            return new SceneGraphWalker(new FakeGameMemory(Window, _bytes), new VtableIndex(symbols), symbols, _layouts, "symbols.txt", limits);
        }

        public SceneGraphResult Walk(WalkLimits? limits = null) => Walker(limits).Walk();

        public VtableScanResult Scan()
        {
            var symbols = new SymbolTable(_symbols);
            return VtableScanner.Scan(new FakeGameMemory(Window, _bytes), new VtableIndex(symbols), symbols,
                name => _layouts.Find(name, VersionMask.Pal)?.VptrOffset, "symbols.txt");
        }

        private ClassLayout Find(string className) => _layouts.Find(className, VersionMask.Pal)!;

        private (uint Sentinel, uint Size, uint ValueOffset) ListOffsets(uint group)
        {
            var layout = Find(_classes[group]);
            var list = layout.Flatten();
            var sentinel = list.First(f => f.Field.Name == "oEnd_");
            var size = list.First(f => f.Field.Name == "mSize");
            return (group + sentinel.AbsoluteOffset!.Value, group + size.AbsoluteOffset!.Value, sentinel.Field.Size!.Value);
        }

        private uint Vtable(string className)
        {
            if (!_vtables.TryGetValue(className, out var address))
            {
                address = VtableBase + (uint)_vtables.Count * 0x40;
                _vtables[className] = address;
                _symbols.Add(new Symbol(Mangle(className), ".data", address, 0x40, "object", "global"));
            }

            return address;
        }

        private static string Mangle(string className)
        {
            var parts = className.Split("::");
            var encoded = string.Concat(parts.Select(p => $"{p.Length}{p}"));
            return parts.Length == 1 ? $"__vt__{encoded}" : $"__vt__Q{parts.Length}{encoded}";
        }
    }
}
