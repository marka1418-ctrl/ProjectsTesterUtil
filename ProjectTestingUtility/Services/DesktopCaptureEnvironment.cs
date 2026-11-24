using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProjectTestingUtility.Services;

/// <summary>
/// Records capture prerequisites without reading account names, addresses or window titles.
/// Null means that Windows did not provide a reliable answer, not that a condition is false.
/// </summary>
public sealed record DesktopCaptureEnvironmentSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }
    public bool WindowsSupported { get; init; }
    public int? ProcessSessionId { get; init; }
    public string? SessionState { get; init; }
    public string? ClientProtocol { get; init; }
    public bool? IsRemoteSession { get; init; }
    public bool? SessionLocked { get; init; }
    public string? ProcessDesktopName { get; init; }
    public bool? InputDesktopAccessible { get; init; }
    public string? InputDesktopName { get; init; }
    public bool? ProcessDesktopIsInputDesktop { get; init; }
    public int? DisplayCount { get; init; }
    public DesktopCaptureDisplayBounds? VirtualDesktop { get; init; }
    public IReadOnlyList<DesktopCaptureQueryError> QueryErrors { get; init; } = [];
}

public sealed record DesktopCaptureDisplayBounds(int X, int Y, int Width, int Height);
public sealed record DesktopCaptureQueryError(string Query, int? NativeErrorCode);

/// <summary>
/// Best-effort diagnostics for the caller's Windows session and desktop. An accessible input
/// desktop does not prove that a capture backend can read its pixels. In particular, neither
/// a failed desktop query nor a different desktop name is treated as proof of a locked session.
/// </summary>
public static class DesktopCaptureEnvironment
{
    public static DesktopCaptureEnvironmentSnapshot Capture()
    {
        var snapshot = new DesktopCaptureEnvironmentSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            WindowsSupported = OperatingSystem.IsWindows()
        };
        if (!snapshot.WindowsSupported)
            return snapshot;

        var errors = new List<DesktopCaptureQueryError>();
        try
        {
            using var process = Process.GetCurrentProcess();
            snapshot = snapshot with { ProcessSessionId = process.SessionId };
        }
        catch (Exception)
        {
            errors.Add(new("ProcessSessionId", null));
        }

        if (snapshot.ProcessSessionId is { } sessionId)
        {
            var state = ReadSessionInteger(sessionId, 8, sizeof(int), "SessionState", errors); // WTSConnectState
            var protocol = ReadSessionInteger(sessionId, 16, sizeof(ushort), "ClientProtocol", errors); // WTSClientProtocolType
            snapshot = snapshot with
            {
                SessionState = SessionStateName(state),
                ClientProtocol = protocol switch { 0 => "Console", 1 => "ICA", 2 => "RDP", null => null, _ => "Unknown" },
                IsRemoteSession = protocol switch { 0 => false, 1 or 2 => true, _ => null },
                SessionLocked = ReadSessionLocked(sessionId, errors)
            };
        }

        try
        {
            // A process can have threads on different desktops. This is the calling thread's
            // desktop, which the recorder's ordinary child-process launch inherits.
            var desktop = GetThreadDesktop(GetCurrentThreadId());
            if (desktop == IntPtr.Zero)
                errors.Add(new("ProcessDesktop", Marshal.GetLastWin32Error()));
            else
                snapshot = snapshot with { ProcessDesktopName = ReadDesktopName(desktop, "ProcessDesktopName", errors) };

            var input = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS only
            snapshot = snapshot with { InputDesktopAccessible = input != IntPtr.Zero };
            if (input == IntPtr.Zero)
                errors.Add(new("InputDesktop", Marshal.GetLastWin32Error()));
            else
            {
                try { snapshot = snapshot with { InputDesktopName = ReadDesktopName(input, "InputDesktopName", errors) }; }
                finally { CloseDesktop(input); }
            }
            if (snapshot.ProcessDesktopName != null && snapshot.InputDesktopName != null)
                snapshot = snapshot with
                {
                    ProcessDesktopIsInputDesktop = string.Equals(snapshot.ProcessDesktopName,
                        snapshot.InputDesktopName, StringComparison.OrdinalIgnoreCase)
                };
        }
        catch (Exception)
        {
            errors.Add(new("DesktopQueries", null));
        }

        try
        {
            // Match the physical coordinate space used by desktop recording on scaled displays.
            var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            if (previous == IntPtr.Zero)
                errors.Add(new("PhysicalDisplayCoordinates", Marshal.GetLastWin32Error()));
            try
            {
                var count = GetSystemMetrics(80); // SM_CMONITORS
                var bounds = new DesktopCaptureDisplayBounds(GetSystemMetrics(76), GetSystemMetrics(77),
                    GetSystemMetrics(78), GetSystemMetrics(79));
                snapshot = snapshot with
                {
                    DisplayCount = count > 0 ? count : null,
                    VirtualDesktop = previous != IntPtr.Zero && bounds.Width > 0 && bounds.Height > 0 ? bounds : null
                };
                if (count <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
                    errors.Add(new("DisplayMetrics", null));
            }
            finally
            {
                if (previous != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previous);
            }
        }
        catch (Exception)
        {
            errors.Add(new("DisplayQueries", null));
        }

        return snapshot with { QueryErrors = errors.ToArray() };
    }

    private static int? ReadSessionInteger(int sessionId, int informationClass, int requiredBytes,
        string query, List<DesktopCaptureQueryError> errors)
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, informationClass, out buffer, out var bytes))
            {
                errors.Add(new(query, Marshal.GetLastWin32Error()));
                return null;
            }
            if (buffer == IntPtr.Zero || bytes < requiredBytes)
            {
                errors.Add(new(query + "Buffer", null));
                return null;
            }
            return requiredBytes == sizeof(ushort) ? (ushort)Marshal.ReadInt16(buffer) : Marshal.ReadInt32(buffer);
        }
        catch (Exception)
        {
            errors.Add(new(query, null));
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                WTSFreeMemory(buffer);
        }
    }

    private static bool? ReadSessionLocked(int sessionId, List<DesktopCaptureQueryError> errors)
    {
        // Windows 7/Server 2008 R2 reversed these flags. Do not interpret that legacy API here.
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return null;
        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 25, out buffer, out var bytes)) // WTSSessionInfoEx
            {
                errors.Add(new("SessionLocked", Marshal.GetLastWin32Error()));
                return null;
            }
            // WTSINFOEX Level is at 0. Its union is aligned to 8 bytes because LEVEL1 contains
            // LARGE_INTEGER fields. Read only the numeric prefix, never the account-name fields.
            if (buffer == IntPtr.Zero || bytes < 20 || Marshal.ReadInt32(buffer) != 1 ||
                Marshal.ReadInt32(buffer, 8) != sessionId)
            {
                errors.Add(new("SessionLockedBuffer", null));
                return null;
            }
            return Marshal.ReadInt32(buffer, 16) switch { 0 => true, 1 => false, _ => null };
        }
        catch (Exception)
        {
            errors.Add(new("SessionLocked", null));
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                WTSFreeMemory(buffer);
        }
    }

    private static string? ReadDesktopName(IntPtr desktop, string query, List<DesktopCaptureQueryError> errors)
    {
        GetUserObjectInformation(desktop, 2, IntPtr.Zero, 0, out var bytes); // UOI_NAME
        if (bytes < 2 || bytes > 65536)
        {
            errors.Add(new(query, Marshal.GetLastWin32Error()));
            return null;
        }
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (GetUserObjectInformation(desktop, 2, buffer, bytes, out _))
                return Marshal.PtrToStringUni(buffer);
            errors.Add(new(query, Marshal.GetLastWin32Error()));
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? SessionStateName(int? state) => state switch
    {
        0 => "Active", 1 => "Connected", 2 => "ConnectQuery", 3 => "Shadow", 4 => "Disconnected",
        5 => "Idle", 6 => "Listen", 7 => "Reset", 8 => "Down", 9 => "Init", null => null, _ => "Unknown"
    };

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int informationClass,
        out IntPtr buffer, out int bytesReturned);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, IntPtr buffer, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
