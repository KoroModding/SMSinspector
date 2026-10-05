using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SMSinspector.Core.Memory;
using SMSinspector.Core.Memory.Windows;

namespace SMSinspector.App.ViewModels;

/// <summary>
/// Connection diagnostics: polls Dolphin once a second and shows where MEM1 is and what
/// its header holds. A read of the header through <see cref="IGameMemory"/> proves that
/// game addresses resolve.
/// </summary>
public sealed partial class DiagnosticViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly DolphinConnection? _connection;
    private readonly DispatcherTimer _timer;
    private bool _polling;

    [ObservableProperty]
    private string _status = "Looking for Dolphin...";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private string _headerDump = "";

    public DiagnosticViewModel()
    {
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

    public void Start()
    {
        if (_connection is null)
        {
            return;
        }

        _timer.Start();
        _ = PollAsync();
    }

    public void Dispose()
    {
        _timer.Stop();
        _connection?.Dispose();
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
            var (status, details, dump) = await Task.Run(() =>
            {
                var s = _connection.Poll();
                return (s, DescribeDetails(s), _connection.Memory is { } memory ? DumpHeader(memory) : "");
            });

            Status = status.Describe();
            IsConnected = status.State == ConnectionState.Connected;
            Details = details;
            HeaderDump = dump;
        }
        finally
        {
            _polling = false;
        }
    }

    private static string DescribeDetails(ConnectionStatus status)
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
