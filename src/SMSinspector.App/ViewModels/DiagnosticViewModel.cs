using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SMSinspector.App.Settings;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Memory.Windows;
using SMSinspector.Core.Symbols;

namespace SMSinspector.App.ViewModels;

/// <summary>
/// Diagnostics for the first milestones: the Dolphin connection, the decomp clone and
/// its symbols, and a live probe that follows gpMarioAddress to Mario and identifies
/// the object from its vtable pointer.
/// </summary>
public sealed partial class DiagnosticViewModel : ObservableObject, IDisposable
{
    private const string MarioGlobal = "gpMarioAddress";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly DolphinConnection? _connection;
    private readonly DispatcherTimer _timer;
    private volatile LoadedDecomp? _decomp;
    private AppSettings _settings;
    private bool _polling;

    [ObservableProperty]
    private string _status = "Looking for Dolphin...";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private string _headerDump = "";

    [ObservableProperty]
    private string _decompPath = "No decomp folder chosen.";

    [ObservableProperty]
    private string _decompStatus = "";

    [ObservableProperty]
    private bool _isDecompLoaded;

    [ObservableProperty]
    private string _marioReport = "";

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

        if (save)
        {
            _settings = _settings with { DecompPath = repository.Root };
            if (!SettingsStore.Save(_settings))
            {
                DecompStatus += Environment.NewLine + $"Could not save the settings in {SettingsStore.DataFolder}.";
            }
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
                    memory is null ? "" : decomp is null ? "Choose the decomp folder to resolve symbols." : DescribeMario(memory, decomp));
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
        var result = GlobalObjectProbe.Probe(memory, decomp.Symbols, decomp.Vtables, MarioGlobal);
        var text = new StringBuilder();

        if (result.Global is { } global)
        {
            text.AppendLine($"{MarioGlobal,-16} {global.Section} 0x{global.Address:X8} = 0x{result.Pointer:X8}");
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
