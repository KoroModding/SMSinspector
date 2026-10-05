using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SMSinspector.Core.Memory.Windows;

/// <summary>Finds Dolphin processes on Windows and opens them with read-only rights.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostProcessSource : IHostProcessSource
{
    // Matches Dolphin.exe and renamed builds. Anything else that matches is filtered out
    // later because it has no MEM1 header.
    private const string ProcessNamePrefix = "Dolphin";

    public WindowsHostProcessSource()
    {
        // Dolphin is a 64-bit process; a 32-bit reader could not address its memory.
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("SMSinspector must run as a 64-bit process.");
        }
    }

    public IReadOnlyList<int> FindDolphinProcessIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.ProcessName.StartsWith(ProcessNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(process.Id);
                }
            }
        }

        return ids;
    }

    public bool TryOpen(int processId, [NotNullWhen(true)] out IHostProcess? process, out HostOpenError error)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ReadOnlyAccess, false, (uint)processId);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError() switch
            {
                NativeMethods.ErrorAccessDenied => HostOpenError.AccessDenied,
                NativeMethods.ErrorInvalidParameter => HostOpenError.NotFound,
                _ => HostOpenError.Other,
            };
            handle.Dispose();
            process = null;
            return false;
        }

        error = HostOpenError.None;
        process = new WindowsHostProcess(processId, handle);
        return true;
    }
}
