using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using CAMAPI.DotnetHelper;
using CAMAPI.ResultStatus;
using CAMIPC.Application;
using CAMIPC.ExecuteContext;
using CAMIPC.Helper;

namespace ProjectTestingUtility.Services;

public sealed record CamWindowActivationResult
{
    public int BackendProcessId { get; init; }
    public int UiProcessId { get; init; }
    public long BackendWindowHandle { get; init; }
    public long WindowHandle { get; init; }
    public bool SdkActivationRequested { get; init; }
    public bool ProjectTabSelected { get; init; }
    public bool IsForeground { get; init; }
    public bool IsVisible { get; init; }
    public string? Warning { get; init; }
}

/// <summary>Shows the UI belonging to the connected CAM backend, without choosing another running project.</summary>
public static class CamWindowActivation
{
    private static readonly object AutomationGate = new();
    private static Task<TabSelection>? _pendingSelection;

    public static CamWindowActivationResult Activate(ComWrapper<ICamIpcApplication> application, string? projectPath = null,
        ComWrapper<IIpcHelper>? ipcHelper = null)
    {
        var result = new CamWindowActivationResult();
        try
        {
            var context = new TExecuteContext();
            var backendPid = application.Invoke(app => app.ProcessId);
            result = result with { BackendProcessId = backendPid };
            using var form = application.InvokeAndWrap(app => app.MainForm);
            if (form == null)
                return result with { Warning = "The connected CAM backend has no main form." };

            // This SDK call addresses the exact connected backend. Foregrounding a
            // process by name would leave Prime's Home page or another tab selected.
            form.Invoke(window => window.SetCurrentInPrime(true));
            result = result with { SdkActivationRequested = true };
            var backendWindow = new IntPtr(form.Invoke(window => window.GetMainWindowHandle(ref context)));
            if (context.ResultStatus.Code == TResultStatusCode.rsError)
                throw new InvalidOperationException(context.ResultStatus.Description);
            result = result with { BackendWindowHandle = backendWindow.ToInt64() };
            var topLevel = FindOwnerWindow(backendWindow, backendPid);
            if (topLevel == IntPtr.Zero)
                return result with { Warning = "No visible window belonging to the connected CAM backend was found." };

            GetWindowThreadProcessId(topLevel, out var uiPid);
            result = result with { WindowHandle = topLevel.ToInt64(), UiProcessId = (int)uiPid };
            BringForward(topLevel);
            var tab = IsWindowVisible(backendWindow)
                ? new TabSelection(true, null)
                : projectPath == null ? new TabSelection(false, null) : SelectProjectTab(topLevel, projectPath);
            if (!IsWindowVisible(backendWindow) && ipcHelper != null &&
                HasOnlyTargetBackend(ipcHelper, (int)uiPid, backendPid))
            {
                var fallback = SelectOnlyProjectTab(topLevel, backendWindow);
                if (fallback.Selected)
                    tab = fallback;
                else
                    tab = tab with { Warning = Join(tab.Warning, fallback.Warning ?? "The only project tab could not be activated.") };
            }
            BringForward(topLevel);
            var foreground = GetForegroundWindow();
            var foregroundRoot = GetAncestor(foreground, 3); // GA_ROOTOWNER includes modal children.
            var isForeground = foreground == topLevel || foregroundRoot == topLevel;
            var isVisible = backendWindow != IntPtr.Zero && IsWindowVisible(backendWindow);
            var warning = tab.Warning;
            if (!isForeground)
                warning = Join(warning, "Windows did not allow CAM to become the foreground window.");
            if (!isVisible)
                warning = Join(warning, "The connected CAM project's window is still hidden; the recording may show Prime's Home page.");
            if (!IsWindowEnabled(topLevel))
                warning = Join(warning, "The CAM window is blocked by a modal dialog; it was left open for diagnosis.");
            return result with
            {
                ProjectTabSelected = tab.Selected,
                IsForeground = isForeground,
                IsVisible = isVisible,
                Warning = warning
            };
        }
        catch (Exception exception)
        {
            return result with { Warning = "Could not activate the tested CAM project: " + exception.Message };
        }
    }

    private static bool HasOnlyTargetBackend(ComWrapper<IIpcHelper> helper, int primePid, int backendPid)
    {
        var context = new TExecuteContext();
        using var prime = helper.InvokeAndWrap(ipc => ipc.GetPrime(primePid, ref context));
        if (prime == null || context.ResultStatus.Code == TResultStatusCode.rsError)
            return false;
        if (!prime.Invoke(value => value.ApplicationBelongs(backendPid, ref context)) ||
            context.ResultStatus.Code == TResultStatusCode.rsError)
            return false;
        using var applications = prime.InvokeAndWrap(value => value.GetApplicationsList(ref context));
        return applications != null && context.ResultStatus.Code != TResultStatusCode.rsError &&
            applications.Invoke(list => list.Count() == 1 && list.Get(0) == backendPid);
    }

    private static TabSelection SelectOnlyProjectTab(IntPtr primeWindow, IntPtr backendWindow)
    {
        // Some Prime versions expose their custom tab strip as an empty MSAA/UIA
        // pane. Clicking is allowed only after IPC proved its sole tab is ours.
        var controls = new List<IntPtr>();
        EnumChildWindows(primeWindow, (child, _) =>
        {
            var name = new System.Text.StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() == "TPrjTabsCtrl" && IsWindowVisible(child))
                controls.Add(child);
            return true;
        }, IntPtr.Zero);
        if (controls.Count != 1 || !IsWindowEnabled(primeWindow))
            return new(false, "The single project tab strip is unavailable or blocked by a dialog.");
        var tabs = controls[0];
        if (!GetClientRect(tabs, out var bounds) || bounds.Right < 80 || bounds.Bottom < 16)
            return new(false, "The project tab strip has no usable bounds.");
        var x = Math.Min(bounds.Right / 4, Math.Max(24, bounds.Bottom));
        var y = bounds.Bottom / 2;
        var point = new IntPtr((y << 16) | (x & 0xffff));
        // Send directly to the verified tab control; never move the user's cursor.
        SendMessageTimeout(tabs, 0x0201, new IntPtr(1), point, 2, 500, out _); // WM_LBUTTONDOWN
        SendMessageTimeout(tabs, 0x0202, IntPtr.Zero, point, 2, 500, out _); // WM_LBUTTONUP
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (IsWindowVisible(backendWindow))
                return new(true, null);
            Thread.Sleep(50);
        }
        return new(false, "The verified single project tab was clicked, but its backend window stayed hidden.");
    }

    private static IntPtr FindOwnerWindow(IntPtr backendWindow, int backendPid)
    {
        if (backendWindow != IntPtr.Zero && IsWindow(backendWindow))
        {
            // Embedded ENCY forms have a parent chain into ency_prime. The chain
            // identifies the owning Prime even when several Prime processes exist.
            var root = GetAncestor(backendWindow, 2); // GA_ROOT
            if (root != IntPtr.Zero && (IsWindowVisible(root) || IsIconic(root)))
                return root;
        }

        var owners = new HashSet<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) && !IsIconic(window))
                return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == backendPid)
                owners.Add(window);
            EnumChildWindows(window, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childPid);
                if (childPid == backendPid)
                    owners.Add(window);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return owners.Count == 1 ? owners.Single() : IntPtr.Zero;
    }

    private static void BringForward(IntPtr window)
    {
        if (IsIconic(window))
            ShowWindowAsync(window, 9); // SW_RESTORE
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static TabSelection SelectProjectTab(IntPtr window, string projectPath)
    {
        using var cancellation = new CancellationTokenSource();
        Task<TabSelection> task;
        lock (AutomationGate)
        {
            if (_pendingSelection is { IsCompleted: false })
                return new(false, "A previous UI Automation request is still pending; project-tab activation was skipped.");
            task = _pendingSelection = Task.Run(() => SelectProjectTabCore(window, projectPath, cancellation.Token));
            _ = task.ContinueWith(completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        try { return task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            return new(false, "CAM's tab interface did not respond within three seconds.");
        }
        catch (Exception exception) { return new(false, "CAM's project tab could not be selected: " + exception.Message); }
    }

    private static TabSelection SelectProjectTabCore(IntPtr window, string projectPath, CancellationToken cancellation)
    {
        var root = AutomationElement.FromHandle(window);
        var elements = root.FindAll(TreeScope.Descendants,
            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
        var candidates = new Dictionary<string, AutomationElement>();
        for (var index = 0; index < elements.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var element = elements[index];
            if (!MatchesProjectLabel(element.Current.Name, projectPath) || element.Current.IsOffscreen)
                continue;
            // Only a real tab or a header button near the top of its owning window
            // is eligible. A similarly named item in Recent projects must not open.
            var top = root.Current.BoundingRectangle.Top;
            for (var level = 0; element != null && level < 4; level++, element = TreeWalker.ControlViewWalker.GetParent(element))
            {
                if (element.Current.ControlType == ControlType.TabItem ||
                    element.Current.ControlType == ControlType.Button && element.Current.BoundingRectangle.Top <= top + 100)
                {
                    if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _) ||
                        element.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                        candidates[string.Join(",", element.GetRuntimeId())] = element;
                    break;
                }
            }
        }
        if (candidates.Count != 1)
            return new(false, candidates.Count == 0
                ? "The target project tab was not exposed by CAM's accessible UI; SDK activation was requested."
                : "Several project tabs have the same label; none was selected automatically.");
        cancellation.ThrowIfCancellationRequested();
        var target = candidates.Values.Single();
        if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            var selector = (SelectionItemPattern)selection;
            if (!selector.Current.IsSelected)
            {
                cancellation.ThrowIfCancellationRequested();
                selector.Select();
            }
            return new(selector.Current.IsSelected, selector.Current.IsSelected ? null : "CAM did not confirm selection of the target project tab.");
        }
        if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            cancellation.ThrowIfCancellationRequested();
            ((InvokePattern)invoke).Invoke();
            return new(false, "The target project header was invoked; its selection state is not exposed by CAM.");
        }
        return new(false, "The project tab has no supported selection action.");
    }

    internal static bool MatchesProjectLabel(string? label, string projectPath)
    {
        var normalized = label?.Trim().TrimEnd('*').Trim();
        return !string.IsNullOrEmpty(normalized) &&
            (string.Equals(normalized, projectPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, Path.GetFileName(projectPath), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, Path.GetFileNameWithoutExtension(projectPath), StringComparison.OrdinalIgnoreCase));
    }

    private static string Join(string? current, string message) => current == null ? message : current + " " + message;
    private sealed record TabSelection(bool Selected, string? Warning);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rectangle bounds);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
