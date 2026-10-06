using System.Diagnostics;
using System.Text;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Discovery;

public enum EdgeKind
{
    /// <summary>The root, reached from the anchors.</summary>
    Root,

    /// <summary>An element of a list container.</summary>
    List,

    /// <summary>A member pointer of the parent.</summary>
    Member,
}

/// <summary>An object of the scene graph.</summary>
/// <param name="ClassName">From the object's vtable symbol.</param>
/// <param name="InstanceName">The name the game gave it (its <c>mName</c>), read from memory; null when empty.</param>
/// <param name="Parent">The object it was first reached from; null for the root.</param>
/// <param name="Via">For a member edge, the parent's member with its header line; for the root, the anchor member.</param>
/// <param name="Index">Position in the parent's list, or element of a member array.</param>
/// <param name="MismatchDeclared">
/// Set when the edge was a type mismatch: the type the list or member declares, which the
/// object's class does not derive from. The object still derives from the graph node class.
/// </param>
public sealed record GraphNode(
    uint Address,
    SourcedName ClassName,
    SourcedName? InstanceName,
    int Depth,
    uint? Parent,
    EdgeKind Edge,
    SourcedName? Via,
    int Index,
    string? MismatchDeclared = null)
{
    public bool IsTypeMismatch => MismatchDeclared is not null;
}

public enum RejectReason
{
    OutsideMem1,
    Misaligned,

    /// <summary>The word where the vtable pointer should be is not a vtable symbol's address.</summary>
    UnknownVtable,

    /// <summary>The object's class does not derive from the graph node class at all.</summary>
    WrongClass,

    /// <summary>The object's class has no layout, so whether it derives from the declared type is unknown.</summary>
    ClassWithoutLayout,

    /// <summary>The declared type has no layout or no vtable pointer, so the target cannot be checked.</summary>
    Unverifiable,
}

/// <param name="Declared">The type the member or list declares.</param>
/// <param name="Actual">The class the target's vtable names, when it has one.</param>
public sealed record RejectedEdge(uint From, uint Target, RejectReason Reason, string Via, string Declared, string? Actual);

/// <param name="MaxDepth">Objects at this depth are kept, their children are not explored.</param>
public sealed record WalkLimits(int MaxDepth, TimeSpan TimeBudget)
{
    public static WalkLimits Default { get; } = new(64, TimeSpan.FromSeconds(2));
}

public sealed record SceneGraphResult(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyDictionary<RejectReason, int> Rejected,
    IReadOnlyList<RejectedEdge> RejectedSamples,
    int NullPointers,
    int DoublePointerMembers,
    int ListSizeMismatches,
    string? Truncated,
    TimeSpan Elapsed,
    string? Error)
{
    private readonly HashSet<uint> _addresses = [.. Nodes.Select(n => n.Address)];

    public bool Contains(uint address) => _addresses.Contains(address);

    public static SceneGraphResult Failed(string error) =>
        new([], new Dictionary<RejectReason, int>(), [], 0, 0, 0, null, TimeSpan.Zero, error);
}

/// <summary>
/// Walks the scene graph (plan 5.4, pass 2) from the anchors: <c>TNameRefGen::instance</c>,
/// its <c>mRootNameRef</c>, then list containers and member pointers to graph nodes. Every
/// offset comes from the PAL layouts; every pointer is checked before it is followed.
/// Read-only.
/// </summary>
public sealed class SceneGraphWalker
{
    public const int MaxSamples = 20;
    private const int MaxNameBytes = 128;

    private static readonly Encoding ShiftJis = CreateShiftJis();

    private readonly IGameMemory _memory;
    private readonly VtableIndex _vtables;
    private readonly SymbolTable _symbols;
    private readonly LoadedLayouts _layouts;
    private readonly WalkLimits _limits;
    private readonly string _symbolsFile;
    private readonly Dictionary<string, ClassPlan?> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _isNode = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, SourcedName> _classNames = [];

    private ClassLayout _nodeLayout = null!;
    private uint _nameOffset;
    private string? _error;

    public SceneGraphWalker(IGameMemory memory, VtableIndex vtables, SymbolTable symbols, LoadedLayouts layouts, string symbolsFile, WalkLimits? limits = null)
    {
        _memory = memory;
        _vtables = vtables;
        _symbols = symbols;
        _layouts = layouts;
        _symbolsFile = symbolsFile;
        _limits = limits ?? WalkLimits.Default;
    }

    private sealed record Candidate(uint Target, ClassLayout Declared, uint From, EdgeKind Edge, SourcedName? Via, int Index, string ViaText);

    private sealed record MemberSlot(uint Offset, int Count, ClassLayout Pointee, SourcedName Member);

    private sealed record ListInfo(uint SentinelOffset, uint SizeOffset, uint NextOffset, uint ValueOffset, ClassLayout Element, SourcedName Via);

    private sealed record ClassPlan(ClassLayout Layout, uint NameOffset, ListInfo? List, IReadOnlyList<MemberSlot> Members, int DoublePointers);

    public SceneGraphResult Walk()
    {
        var watch = Stopwatch.StartNew();
        if (!TryResolveRoot(out var root, out var error))
        {
            return SceneGraphResult.Failed(error);
        }

        var nodes = new List<GraphNode>();
        var rejected = new Dictionary<RejectReason, int>();
        var samples = new List<RejectedEdge>();
        var visited = new HashSet<uint>();
        var queue = new Queue<(Candidate Edge, ClassLayout Layout, int Depth, bool Mismatch)>();
        int nulls = 0, doublePointers = 0, sizeMismatches = 0;
        var cutByDepth = 0;
        string? truncated = null;

        void Offer(Candidate candidate, int depth)
        {
            if (candidate.Target == 0)
            {
                nulls++;
                return;
            }

            // A cycle or a second path: the first one found stays the parent.
            if (visited.Contains(candidate.Target))
            {
                return;
            }

            if (!TryCheck(candidate, out var layout, out var reason, out var actual, out var mismatch))
            {
                rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
                if (samples.Count < MaxSamples)
                {
                    samples.Add(new RejectedEdge(candidate.From, candidate.Target, reason, candidate.ViaText, candidate.Declared.Name, actual));
                }

                return;
            }

            visited.Add(candidate.Target);
            queue.Enqueue((candidate, layout, depth, mismatch));
        }

        Offer(root, 0);
        while (queue.Count > 0)
        {
            if (watch.Elapsed > _limits.TimeBudget)
            {
                truncated = $"time budget of {_limits.TimeBudget.TotalMilliseconds:N0} ms reached";
                break;
            }

            var (edge, layout, depth, isMismatch) = queue.Dequeue();
            var plan = Plan(layout);
            if (_error is not null)
            {
                return SceneGraphResult.Failed(_error);
            }

            nodes.Add(new GraphNode(edge.Target, ClassName(edge.Target, layout), ReadName(edge.Target, plan), depth,
                edge.Edge == EdgeKind.Root ? null : edge.From, edge.Edge, edge.Via, edge.Index, isMismatch ? edge.Declared.Name : null));

            if (plan is null)
            {
                continue;
            }

            if (depth >= _limits.MaxDepth)
            {
                cutByDepth++;
                continue;
            }

            doublePointers += plan.DoublePointers;
            if (plan.List is { } list && !ExpandList(edge.Target, list, depth, Offer))
            {
                sizeMismatches++;
            }

            foreach (var slot in plan.Members)
            {
                for (var i = 0; i < slot.Count; i++)
                {
                    var address = edge.Target + slot.Offset + (uint)(i * 4);
                    if (_memory.TryReadU32(address, out var target))
                    {
                        Offer(new Candidate(target, slot.Pointee, edge.Target, EdgeKind.Member, slot.Member, i, $"{layout.Name}::{slot.Member.Value}"), depth + 1);
                    }
                }
            }
        }

        if (truncated is null && cutByDepth > 0)
        {
            truncated = $"depth limit of {_limits.MaxDepth} reached at {cutByDepth:N0} objects";
        }

        if (truncated is not null)
        {
            truncated += $"; {nodes.Count:N0} objects reached before the cut";
        }

        return new SceneGraphResult(nodes, rejected, samples, nulls, doublePointers, sizeMismatches, truncated, watch.Elapsed, null);
    }

    private bool TryResolveRoot(out Candidate root, out string error)
    {
        root = null!;
        if (_layouts.Find(Anchors.GraphNode.Name, VersionMask.Pal) is not { HasVptr: true } nodeLayout)
        {
            error = $"Anchor {Anchors.GraphNode.Name} does not resolve to a class with a vtable.";
            return false;
        }

        _nodeLayout = nodeLayout;
        var (nameOwner, nameMember) = Anchors.Split(Anchors.InstanceName);
        if (nameOwner != Anchors.GraphNode.Name || OwnField(nodeLayout, nameMember) is not { Offset: { } nameOffset })
        {
            error = $"Anchor {Anchors.InstanceName.Name} does not resolve.";
            return false;
        }

        _nameOffset = nameOffset;
        var (genOwner, rootMember) = Anchors.Split(Anchors.SceneGraphRoot);
        if (!_symbols.TryGetUnique(Anchors.SceneGraphInstance.Name, out var instance))
        {
            error = $"Anchor {Anchors.SceneGraphInstance.Name} is not a unique symbol of symbols.txt.";
            return false;
        }

        if (_layouts.Find(genOwner, VersionMask.Pal) is not { } genLayout
            || genLayout.Flatten().FirstOrDefault(f => f.Field.Name == rootMember && f.Owner.Name == genOwner) is not { AbsoluteOffset: { } rootOffset } rootField)
        {
            error = $"Anchor {Anchors.SceneGraphRoot.Name} does not resolve.";
            return false;
        }

        if (!_memory.TryReadU32(instance.Address, out var generator) || generator == 0 || !GameCube.IsMem1Address(generator))
        {
            error = $"{Anchors.SceneGraphInstance.Name} holds no object (0x{generator:X8}): is a level loaded?";
            return false;
        }

        if (!_memory.TryReadU32(generator + rootOffset, out var rootAddress))
        {
            error = $"Could not read {Anchors.SceneGraphRoot.Name}.";
            return false;
        }

        root = new Candidate(rootAddress, nodeLayout, generator, EdgeKind.Root, rootField.Field.Identity, 0, Anchors.SceneGraphRoot.Name);
        error = "";
        return true;
    }

    /// <param name="mismatch">
    /// True when the class does not derive from the declared type but does derive from the
    /// graph node class: the edge is followed and marked, since the game stores objects in
    /// lists and members more loosely than the headers declare.
    /// </param>
    private bool TryCheck(Candidate candidate, out ClassLayout layout, out RejectReason reason, out string? actualClass, out bool mismatch)
    {
        layout = null!;
        actualClass = null;
        mismatch = false;
        var target = candidate.Target;
        if (!GameCube.IsMem1Address(target))
        {
            reason = RejectReason.OutsideMem1;
            return false;
        }

        if (target % 4 != 0)
        {
            reason = RejectReason.Misaligned;
            return false;
        }

        if (candidate.Declared is not { HasVptr: true, VptrOffset: { } vptrOffset })
        {
            reason = RejectReason.Unverifiable;
            return false;
        }

        // The vtable pointer must be a vtable symbol's address exactly; a pointer into the
        // middle of one belongs to a base subobject or to nothing.
        if (!_memory.TryReadU32(target + vptrOffset, out var vptr)
            || !_vtables.TryResolve(vptr, out var vtable, out var into) || into != 0)
        {
            reason = RejectReason.UnknownVtable;
            return false;
        }

        actualClass = vtable.ClassName;
        if (_layouts.Find(vtable.ClassName, VersionMask.Pal) is not { } actual)
        {
            reason = RejectReason.ClassWithoutLayout;
            return false;
        }

        if (!DerivesFrom(actual, candidate.Declared.Name))
        {
            if (!DerivesFrom(actual, _nodeLayout.Name))
            {
                reason = RejectReason.WrongClass;
                return false;
            }

            mismatch = true;
        }

        _classNames.TryAdd(target, new SourcedName(vtable.ClassName, Provenance.Symbol(_symbolsFile, vtable.Symbol.Name, vtable.Symbol.Address)));
        layout = actual;
        reason = default;
        return true;
    }

    private bool ExpandList(uint owner, ListInfo list, int depth, Action<Candidate, int> offer)
    {
        var sentinel = owner + list.SentinelOffset;
        if (!_memory.TryReadU32(owner + list.SizeOffset, out var size) || !_memory.TryReadU32(sentinel + list.NextOffset, out var node))
        {
            return false;
        }

        var index = 0;
        while (node != sentinel)
        {
            // A broken or recycled list: stop rather than wander through memory.
            if (index > size || !GameCube.IsMem1Address(node) || node % 4 != 0)
            {
                return false;
            }

            if (_memory.TryReadU32(node + list.ValueOffset, out var value))
            {
                offer(new Candidate(value, list.Element, owner, EdgeKind.List, list.Via, index, $"list of 0x{owner:X8}"), depth + 1);
            }

            if (!_memory.TryReadU32(node + list.NextOffset, out node))
            {
                return false;
            }

            index++;
        }

        return index == size;
    }

    private ClassPlan? Plan(ClassLayout layout)
    {
        if (_plans.TryGetValue(layout.Name, out var plan))
        {
            return plan;
        }

        var nameBase = BaseOffset(layout, _nodeLayout.Name);
        var members = new List<MemberSlot>();
        var doublePointers = 0;
        foreach (var flat in layout.Flatten())
        {
            if (flat.Field.Member is not { Kind: MemberKind.Data } member || member.Type.PointerDepth == 0 || member.Type.IsFunctionPointer
                || flat.AbsoluteOffset is not { } offset || flat.Field.Size is not { } size)
            {
                continue;
            }

            if (_layouts.Engine.GetMemberTypeLayout(flat.Owner, flat.Field, VersionMask.Pal) is not { } pointee || !IsNode(pointee))
            {
                continue;
            }

            if (member.Type.PointerDepth > 1)
            {
                doublePointers++;
                continue;
            }

            members.Add(new MemberSlot(offset, (int)(size / 4), pointee, flat.Field.Identity));
        }

        plan = nameBase is { } b ? new ClassPlan(layout, b + _nameOffset, ListOf(layout), members, doublePointers) : null;
        _plans[layout.Name] = plan;
        return plan;
    }

    private ListInfo? ListOf(ClassLayout layout)
    {
        var prefix = Anchors.ListContainer.Name + "<";
        if (FindBase(layout, prefix, 0) is not ({ } container, var containerOffset))
        {
            return null;
        }

        if (!HeaderParser.TryParseType(container.Name, out var type) || type.TemplateArgs is not [{ Type: { PointerDepth: 1 } element }]
            || _layouts.Find(element.BaseName, VersionMask.Pal) is not { } elementLayout)
        {
            _error = $"Anchor {Anchors.ListContainer.Name}: cannot read the element type of {container.Name}.";
            return null;
        }

        var (listOwner, sizeName) = Anchors.Split(Anchors.ListSize);
        var (_, sentinelName) = Anchors.Split(Anchors.ListSentinel);
        var (_, nextName) = Anchors.Split(Anchors.ListNodeNext);
        if (FindBase(container, listOwner + "<", containerOffset) is not ({ } list, var listOffset)
            || OwnField(list, sizeName) is not { Offset: { } sizeOffset }
            || OwnField(list, sentinelName) is not { Offset: { } sentinelOffset, Size: { } nodeSize } sentinel
            || _layouts.Engine.GetMemberTypeLayout(list, sentinel, VersionMask.Pal) is not { } node
            || OwnField(node, nextName) is not { Offset: { } nextOffset })
        {
            _error = $"The list anchors ({Anchors.ListSize.Name}, {Anchors.ListSentinel.Name}, {Anchors.ListNodeNext.Name}) do not resolve in {container.Name}.";
            return null;
        }

        // The stored value follows the node header: "(T*)(node + 1)" in the JGadget source.
        return new ListInfo(listOffset + sentinelOffset, listOffset + sizeOffset, nextOffset, nodeSize, elementLayout, sentinel.Identity);
    }

    private SourcedName ClassName(uint address, ClassLayout layout) =>
        _classNames.TryGetValue(address, out var name) ? name : layout.Identity;

    private SourcedName? ReadName(uint address, ClassPlan? plan)
    {
        if (plan is null || !_memory.TryReadU32(address + plan.NameOffset, out var pointer) || pointer == 0 || !GameCube.IsMem1Address(pointer))
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[(int)Math.Min(MaxNameBytes, GameCube.Mem1End - pointer)];
        if (!_memory.TryRead(pointer, bytes))
        {
            return null;
        }

        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return length == 0 ? null : new SourcedName(ShiftJis.GetString(bytes[..length]), Provenance.Memory(pointer, $"instance name of 0x{address:X8}"));
    }

    /// <summary>True for a class derived from the graph node anchor; false when it has no layout.</summary>
    public bool IsGraphNodeClass(string className) =>
        _layouts.Find(Anchors.GraphNode.Name, VersionMask.Pal) is { } node
        && _layouts.Find(className, VersionMask.Pal) is { } layout
        && DerivesFrom(layout, node.Name);

    private bool IsNode(ClassLayout layout)
    {
        if (!_isNode.TryGetValue(layout.Name, out var result))
        {
            result = DerivesFrom(layout, _nodeLayout.Name);
            _isNode[layout.Name] = result;
        }

        return result;
    }

    private static bool DerivesFrom(ClassLayout layout, string name) =>
        layout.Name == name || layout.Bases.Any(b => DerivesFrom(b.Layout, name));

    private static uint? BaseOffset(ClassLayout layout, string name)
    {
        if (layout.Name == name)
        {
            return 0;
        }

        foreach (var b in layout.Bases)
        {
            if (b.Offset is { } offset && BaseOffset(b.Layout, name) is { } inner)
            {
                return offset + inner;
            }
        }

        return null;
    }

    private static (ClassLayout? Layout, uint Offset) FindBase(ClassLayout layout, string prefix, uint origin)
    {
        foreach (var b in layout.Bases)
        {
            if (b.Offset is not { } offset)
            {
                continue;
            }

            if (b.Layout.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (b.Layout, origin + offset);
            }

            if (FindBase(b.Layout, prefix, origin + offset) is ({ } found, var at))
            {
                return (found, at);
            }
        }

        return (null, 0);
    }

    private static FieldLayout? OwnField(ClassLayout layout, string name) => layout.Fields.FirstOrDefault(f => f.Name == name);

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }
}

/// <summary>The scan and the walk side by side (plan 5.4: merge both passes).</summary>
/// <param name="NotReached">Scanned objects of graph node classes the walk did not reach: possibly stale.</param>
/// <param name="OutsideGraph">Scanned objects of classes that are not graph nodes at all.</param>
public sealed record DiscoveryMerge(IReadOnlyList<FoundObject> NotReached, IReadOnlyList<FoundObject> OutsideGraph)
{
    public static DiscoveryMerge Build(SceneGraphResult graph, VtableScanResult scan, Func<string, bool> isGraphNodeClass)
    {
        var isNode = new Dictionary<string, bool>(StringComparer.Ordinal);
        var notReached = new List<FoundObject>();
        var outside = new List<FoundObject>();
        foreach (var found in scan.Objects)
        {
            if (!isNode.TryGetValue(found.ClassName.Value, out var node))
            {
                isNode[found.ClassName.Value] = node = isGraphNodeClass(found.ClassName.Value);
            }

            if (!node)
            {
                outside.Add(found);
            }
            else if (!graph.Contains(found.Address))
            {
                notReached.Add(found);
            }
        }

        return new DiscoveryMerge(notReached, outside);
    }
}

public static class SceneGraphText
{
    public const string NotReachedLabel = "not reached (possibly stale)";

    public static string Describe(SceneGraphResult graph, DiscoveryMerge? merge = null, int treeDepth = 2, int topClasses = 15)
    {
        if (graph.Error is { } error)
        {
            return $"Scene graph not walked: {error}";
        }

        var lines = new List<string>
        {
            $"Scene graph: {graph.Nodes.Count:N0} objects in {graph.Elapsed.TotalMilliseconds:N0} ms, {graph.Nodes.Count(n => n.Edge == EdgeKind.List):N0} reached through lists and {graph.Nodes.Count(n => n.Edge == EdgeKind.Member):N0} through member pointers.",
        };

        if (graph.Truncated is { } truncated)
        {
            lines.Add($"Walk cut short: {truncated}.");
        }

        var rejected = graph.Rejected.Count == 0
            ? "none"
            : string.Join(", ", graph.Rejected.OrderByDescending(r => r.Value).Select(r => $"{r.Value:N0} {ReasonText(r.Key)}"));
        lines.Add($"Rejected edges, not followed: {rejected}.");
        var mismatches = graph.Nodes.Where(n => n.IsTypeMismatch).ToList();
        lines.Add($"Followed with a type mismatch (class not derived from the declared type): {mismatches.Count:N0}.");
        foreach (var group in mismatches.GroupBy(n => (n.MismatchDeclared, n.ClassName.Value)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Value, StringComparer.Ordinal).Take(topClasses))
        {
            lines.Add($"  {group.Count(),6:N0}  declared {group.Key.MismatchDeclared}, found {group.Key.Value}");
        }

        lines.Add($"Not followed: {graph.DoublePointerMembers:N0} T** members. Null pointers: {graph.NullPointers:N0}. Lists whose length disagrees with their size: {graph.ListSizeMismatches:N0}.");

        foreach (var sample in graph.RejectedSamples.Take(5))
        {
            lines.Add($"  e.g. 0x{sample.From:X8} -> 0x{sample.Target:X8} via {sample.Via}: {ReasonText(sample.Reason)} (declared {sample.Declared}{(sample.Actual is { } actual ? $", found {actual}" : "")})");
        }

        if (merge is not null)
        {
            lines.Add($"Scanned objects of graph classes, {NotReachedLabel}: {merge.NotReached.Count:N0}.");
            foreach (var group in merge.NotReached.GroupBy(o => o.ClassName.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(topClasses))
            {
                lines.Add($"  {group.Count(),6:N0}  {group.Key}");
            }

            lines.Add($"Scanned objects outside the scene graph (not {Anchors.GraphNode.Name}): {merge.OutsideGraph.Count:N0}.");
        }

        lines.Add("Top of the tree:");
        var children = graph.Nodes.Where(n => n.Parent is not null).ToLookup(n => n.Parent!.Value);
        void Print(GraphNode node)
        {
            var name = node.InstanceName is { } instance ? $" \"{instance.Value}\"" : "";
            lines.Add($"{new string(' ', 2 + node.Depth * 2)}{node.ClassName.Value}{name} 0x{node.Address:X8}");
            if (node.Depth < treeDepth)
            {
                foreach (var child in children[node.Address])
                {
                    Print(child);
                }
            }
        }

        foreach (var root in graph.Nodes.Where(n => n.Edge == EdgeKind.Root))
        {
            Print(root);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string ReasonText(RejectReason reason) => reason switch
    {
        RejectReason.OutsideMem1 => "outside MEM1",
        RejectReason.Misaligned => "misaligned",
        RejectReason.UnknownVtable => "no known vtable",
        RejectReason.WrongClass => $"class not derived from {Anchors.GraphNode.Name}",
        RejectReason.ClassWithoutLayout => "class without a layout",
        RejectReason.Unverifiable => "declared type cannot be checked",
        _ => reason.ToString(),
    };
}
