using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SMSinspector.App.Settings;
using SMSinspector.Core;
using SMSinspector.Core.Discovery;
using SMSinspector.Core.Layouts;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Memory.Windows;
using SMSinspector.Core.Names;
using SMSinspector.Core.Symbols;

namespace SMSinspector.App.ViewModels;

/// <summary>
/// Diagnostics for the first milestones: the Dolphin connection, the decomp clone with
/// its symbols and class layouts, the name extractor, and a live probe that follows
/// gpMarioAddress to Mario and identifies the object from its vtable pointer.
/// </summary>
public sealed partial class DiagnosticViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly DolphinConnection? _connection;
    private readonly DispatcherTimer _timer;
    private volatile LoadedDecomp? _decomp;
    private LoadedLayouts? _layouts;
    private ExtractionReport? _names;
    private AppSettings _settings;
    private bool _polling;

    [ObservableProperty]
    private string _status = "Looking for Dolphin...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMario))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanStatus = "";

    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private string _headerDump = "";

    [ObservableProperty]
    private string _decompPath = "No decomp folder chosen.";

    // Without a decomp nothing of the game is named, the anchors included (plan 5.8).
    [ObservableProperty]
    private string _decompStatus = "decomp folder required";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMario))]
    [NotifyPropertyChangedFor(nameof(MarioCaption))]
    private bool _isDecompLoaded;

    [ObservableProperty]
    private string _marioReport = "";

    [ObservableProperty]
    private string _layoutStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    private bool _areLayoutsLoaded;

    [ObservableProperty]
    private string _classQuery = "";

    [ObservableProperty]
    private string _classLayoutText = "";

    [ObservableProperty]
    private string _reportPath = "";

    [ObservableProperty]
    private string _namesStatus = "";

    [ObservableProperty]
    private bool _isExtracting;

    [ObservableProperty]
    private bool _hasNames;

    [ObservableProperty]
    private string _namesReportPath = "";

    /// <summary>The scan names classes, so it needs the decomp, and the layouts for the vptr offsets.</summary>
    public bool CanScan => IsConnected && AreLayoutsLoaded && !IsScanning;

    /// <summary>The Mario probe names an anchor, so it only shows once the decomp is loaded.</summary>
    public bool ShowMario => IsConnected && IsDecompLoaded;

    public string MarioCaption => IsDecompLoaded ? $"{Anchors.MarioPointer.Name}, followed live to the object and its vtable" : "";

    public DiagnosticViewModel()
    {
        _settings = SettingsStore.Load();

        if (OperatingSystem.IsWindows())
        {
            _connection = new DolphinConnection(new WindowsHostProcessSource());
        }
        else
        {
            Status = "Only Windows is supported for now.";
        }

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public async Task StartAsync()
    {
        if (_settings.DecompPath is { } saved)
        {
            await LoadDecompAsync(saved, save: false);
        }

        if (_connection is null)
        {
            return;
        }

        _timer.Start();
        await PollAsync();
    }

    /// <summary>Called when the user picks a folder. Saves it only if it is a usable clone.</summary>
    public Task ChooseDecompFolderAsync(string path) => LoadDecompAsync(path, save: true);

    public void Dispose()
    {
        _timer.Stop();
        _connection?.Dispose();
    }

    private async Task LoadDecompAsync(string path, bool save)
    {
        DecompPath = path;

        if (!DecompRepository.TryOpen(path, GameVersion.GMSP01, out var repository, out var error))
        {
            DecompStatus = error;
            IsDecompLoaded = false;
            _decomp = null;
            return;
        }

        DecompStatus = "Loading symbols...";
        try
        {
            var decomp = await Task.Run(() => LoadedDecomp.Load(repository, GameVersion.GMSP01));
            _decomp = decomp;
            IsDecompLoaded = true;
            DecompStatus = DescribeDecomp(decomp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _decomp = null;
            IsDecompLoaded = false;
            DecompStatus = $"Could not read symbols.txt: {e.Message}";
            return;
        }

        _ = LoadLayoutsAsync(repository);

        if (save)
        {
            _settings = _settings with { DecompPath = repository.Root };
            if (!SettingsStore.Save(_settings))
            {
                DecompStatus += Environment.NewLine + $"Could not save the settings in {SettingsStore.DataFolder}.";
            }
        }
    }

    /// <summary>Shows the PAL layout of the class named in <see cref="ClassQuery"/>.</summary>
    public void ShowClass()
    {
        if (_layouts is null)
        {
            ClassLayoutText = "Layouts are not loaded yet.";
            return;
        }

        // The layout engine is not thread-safe and the extractor is using it.
        if (IsExtracting)
        {
            ClassLayoutText = "The name extractor is running; try again when it is done.";
            return;
        }

        var layout = _layouts.Find(ClassQuery, VersionMask.Pal);
        ClassLayoutText = layout is null
            ? $"No class named '{ClassQuery.Trim()}' (templates need their arguments, as in Name<f32>)."
            : LayoutText.Describe(layout);
    }

    /// <summary>Writes the full validation report to the reports folder in the user data folder.</summary>
    public void SaveReport()
    {
        if (_layouts is null || _decomp is not { } decomp)
        {
            return;
        }

        var commit = decomp.CommitHash?[..12];
        var folder = Path.Combine(SettingsStore.DataFolder, "reports");
        var path = Path.Combine(folder, $"layout-report-{commit ?? "unknown"}.txt");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, _layouts.Report.ToText(commit));
            ReportPath = $"Saved to {path}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ReportPath = $"Could not save the report: {e.Message}";
        }
    }

    /// <summary>
    /// Runs the name extractor (plan 5.7). The sources are read again on every run, so a
    /// main.dol or linker map added to the clone is picked up without restarting.
    /// </summary>
    public async Task RunNameExtractorAsync()
    {
        if (_layouts is not { } layouts || _decomp is not { } decomp || IsExtracting)
        {
            return;
        }

        IsExtracting = true;
        NamesStatus = "Reading the decomp sources and main.dol...";
        NamesReportPath = "";
        try
        {
            // The extractor runs the main.dol check again, so the layout report is rebuilt with it.
            var (names, report) = await Task.Run(() =>
            {
                var result = NameExtractor.Run(layouts.Engine, NameSourceLoader.Load(decomp));
                return (result, LayoutReport.Build(layouts.Catalog, layouts.Engine));
            });
            _names = names;
            _layouts = layouts with { Report = report };
            LayoutStatus = report.Summary() + Environment.NewLine + names.LayoutCheck.Summary();
            HasNames = true;
            NamesStatus = names.Summary();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _names = null;
            HasNames = false;
            NamesStatus = $"Could not read the decomp sources: {e.Message}";
        }
        finally
        {
            IsExtracting = false;
        }
    }

    /// <summary>Writes the name extractor report, as text and JSON, to the reports folder.</summary>
    public void SaveNamesReport()
    {
        if (_names is null || _decomp is not { } decomp)
        {
            return;
        }

        var commit = decomp.CommitHash?[..12];
        var folder = Path.Combine(SettingsStore.DataFolder, "reports");
        var basePath = Path.Combine(folder, $"name-report-{commit ?? "unknown"}");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(basePath + ".txt", _names.ToText(commit));
            File.WriteAllText(basePath + ".json", _names.ToJson(commit));
            NamesReportPath = $"Saved to {basePath}.txt and .json";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            NamesReportPath = $"Could not save the report: {e.Message}";
        }
    }

    /// <summary>
    /// Finds every polymorphic object in MEM1 by its vtable pointer (plan 5.4, pass 1), and
    /// checks the result against the object gpMarioAddress points to. Read-only.
    /// </summary>
    public async Task ScanVtablesAsync()
    {
        if (_connection is null || _layouts is not { } layouts || _decomp is not { } decomp || IsScanning)
        {
            return;
        }

        // The layout engine is not thread-safe: work out every vptr offset here, before going
        // to the background.
        var offsets = decomp.Vtables.All
            .Select(v => v.ClassName)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => layouts.Find(name, VersionMask.Pal) is { HasVptr: true } layout ? layout.VptrOffset : null, StringComparer.Ordinal);

        // Hold off polling: the connection is not thread-safe either.
        while (_polling)
        {
            await Task.Delay(20);
        }

        _polling = true;
        IsScanning = true;
        ScanStatus = "Scanning MEM1...";
        try
        {
            ScanStatus = await Task.Run(() =>
            {
                if (_connection.Memory is not { } memory)
                {
                    return "Not connected to the game.";
                }

                var symbolsFile = Path.GetRelativePath(decomp.Repository.Root, decomp.Repository.SymbolsPath(decomp.Version)).Replace('\\', '/');
                var result = VtableScanner.Scan(memory, decomp.Vtables, decomp.Symbols, name => offsets.GetValueOrDefault(name), symbolsFile);
                var probe = GlobalObjectProbe.Probe(memory, decomp.Symbols, decomp.Vtables, Anchors.MarioPointer.Name);
                var found = result.At(probe.Pointer);
                var check = probe.Global is null
                    ? $"{Anchors.MarioPointer.Name}: {probe.Message}"
                    : $"{Anchors.MarioPointer.Name} -> 0x{probe.Pointer:X8}: " + (found is null ? "not found by the scan." : $"found by the scan as {found.ClassName.Value}.");
                return VtableScanText.Describe(result) + Environment.NewLine + check;
            });
        }
        finally
        {
            IsScanning = false;
            _polling = false;
        }
    }

    private async Task LoadLayoutsAsync(DecompRepository repository)
    {
        AreLayoutsLoaded = false;
        HasNames = false;
        _names = null;
        NamesStatus = "";
        LayoutStatus = "Parsing headers...";
        try
        {
            var decomp = _decomp;
            var (layouts, check) = await Task.Run(() =>
            {
                var loaded = LoadedLayouts.Load(repository);
                if (decomp is null)
                {
                    return (loaded, LayoutCheckResult.NotRun);
                }

                // The game's code can contradict a PAL layout; check before anything is shown.
                var result = DolLayoutCheck.Apply(loaded.Engine, NameSourceLoader.Load(decomp));
                return (loaded with { Report = LayoutReport.Build(loaded.Catalog, loaded.Engine) }, result);
            });
            _layouts = layouts;
            AreLayoutsLoaded = true;
            LayoutStatus = layouts.Report.Summary() + Environment.NewLine + check.Summary();
            ShowClass();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _layouts = null;
            LayoutStatus = $"Could not read the headers: {e.Message}";
        }
    }

    private async Task PollAsync()
    {
        if (_polling || _connection is null)
        {
            return;
        }

        _polling = true;
        try
        {
            // Scanning the process regions can take a few milliseconds; keep it off the UI thread.
            var decomp = _decomp;
            var (status, details, dump, mario) = await Task.Run(() =>
            {
                var s = _connection.Poll();
                var memory = _connection.Memory;
                return (
                    s,
                    DescribeConnection(s),
                    memory is null ? "" : DumpHeader(memory),
                    memory is null || decomp is null ? "" : DescribeMario(memory, decomp));
            });

            Status = status.Describe();
            IsConnected = status.State == ConnectionState.Connected;
            Details = details;
            HeaderDump = dump;
            MarioReport = mario;
        }
        finally
        {
            _polling = false;
        }
    }

    private static string DescribeDecomp(LoadedDecomp decomp)
    {
        var commit = decomp.CommitHash is { } hash ? hash[..12] : "unknown";
        var text = new StringBuilder();
        text.AppendLine($"Commit       {commit}");
        text.AppendLine($"Symbols      {decomp.Symbols.Count:N0} from config/{decomp.Version}/symbols.txt" +
            (decomp.SkippedSymbolLines > 0 ? $" ({decomp.SkippedSymbolLines} unreadable lines skipped)" : ""));
        text.AppendLine($"Vtables      {decomp.Vtables.Count:N0}");
        text.Append($"Linker map   {decomp.LinkerMap.Message}");
        return text.ToString();
    }

    private static string DescribeConnection(ConnectionStatus status)
    {
        if (status.Location is not { } location)
        {
            return "";
        }

        return $"""
            Process ID   {status.ProcessId}
            Game ID      {location.GameId}
            Revision     {location.Revision}
            MEM1 (host)  0x{location.HostBase:X16}
            MEM1 (game)  0x{GameCube.Mem1Base:X8} to 0x{GameCube.Mem1End - 1:X8}
            """;
    }

    private static string DescribeMario(IGameMemory memory, LoadedDecomp decomp)
    {
        var anchor = Anchors.MarioPointer.Name;
        var result = GlobalObjectProbe.Probe(memory, decomp.Symbols, decomp.Vtables, anchor);
        var text = new StringBuilder();

        if (result.Global is { } global)
        {
            text.AppendLine($"{anchor,-16} {global.Section} 0x{global.Address:X8} = 0x{result.Pointer:X8}");
        }

        if (result.Identity is { } identity)
        {
            text.AppendLine($"vptr (+0x{identity.VptrOffset:X})      0x{identity.Vptr:X8}");
            if (identity.Vtable is { } vtable)
            {
                var size = vtable.Symbol.Size is { } s ? $", size 0x{s:X}" : "";
                text.AppendLine($"vtable symbol    {vtable.Symbol.Name} at 0x{vtable.Symbol.Address:X8}{size}");
                text.AppendLine($"vptr - symbol    0x{identity.OffsetIntoVtable:X}");
                text.AppendLine($"class            {identity.ClassName}");
            }
        }

        text.Append(result.Message);
        return text.ToString();
    }

    private static string DumpHeader(IGameMemory memory)
    {
        Span<byte> bytes = stackalloc byte[Mem1Header.Length];
        if (!memory.TryRead(GameCube.Mem1Base, bytes))
        {
            return "Header read failed.";
        }

        var text = new StringBuilder();
        for (var row = 0; row < bytes.Length; row += 16)
        {
            text.Append($"{GameCube.Mem1Base + (uint)row:X8} ");
            foreach (var b in bytes.Slice(row, 16))
            {
                text.Append($" {b:X2}");
            }

            text.Append("  ");
            foreach (var b in bytes.Slice(row, 16))
            {
                text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }
}
