using CAMAPI.DotnetHelper;
using CAMAPI.ResultStatus;
using CAMAPI.TechOperation;
using CAMHelper.NativeLibUtils;
using CAMIPC.Application;
using CAMIPC.ExecuteContext;
using CAMIPC.FunctionalTest;
using CAMIPC.Helper;
using CAMIPC.Technologist;
using CAMIPC.TechOperation;
using NLog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ProjectTestingUtility.Models;
using ProjectTestingUtility.Services;

namespace ProjectTestingUtility.CAMConnector;

public class CamUtilityHelper(SettingsService settingsService, SimulationService simulationService, TechnologyService technologyService) : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly object NativeLibraryGate = new();
    private static readonly Dictionary<string, IntPtr> LoadedLibraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Settings _settings = settingsService.Settings;
    private ComWrapper<IIpcHelper>? _helper;
    private ComWrapper<ICamIpcApplication>? _application;
    private ComWrapper<IIpcFunctionalTest>? _applicationTest;
    private IntPtr _helperDllPtr;
    private TExecuteContext _executeContext = new();
    private delegate IntPtr CreateHelperDelegate();

    public ComWrapper<ICamIpcApplication> GetApplication() => _application ?? throw new Exception("Application not found");
    public ComWrapper<IIpcHelper> GetIpcHelper() => _helper ?? throw new Exception("IPC helper not found");
    public string? GetNativeLogPath()
    {
        var path = GetApplication().Invoke(a => a.GetLogFilePath(ref _executeContext));
        if (string.IsNullOrWhiteSpace(path)) return null;
        using var paths = GetApplication().InvokeAndWrap(a => a.Paths);
        return paths.Invoke(p => p.TryUnfoldPath(path));
    }
    public string? GetCamConfigurationFolder()
    {
        using var paths = GetApplication().InvokeAndWrap(a => a.Paths);
        return paths.Invoke(p => p.TryUnfoldPath(p.ConfigFolder));
    }

    public void Initialize(string camFolder, int processId)
    {
        Logger.Debug("Initialize CAM helper");
        var helperPath = Path.Combine(camFolder, "CAMIPC.Helper.Cam.dll");
        if (!File.Exists(helperPath)) throw new Exception($"{helperPath} not found");

        // fill object to connect to CAM application
        lock (NativeLibraryGate)
        {
            if (!LoadedLibraries.TryGetValue(helperPath, out _helperDllPtr))
            {
                _helperDllPtr = NativeLibLoader.LoadDll(helperPath, out var resultLoadDll);
                if (_helperDllPtr == IntPtr.Zero || resultLoadDll != 0)
                    throw new Exception($"Error loading: {resultLoadDll}");
                // COM getter RCWs can outlive a helper. Keep their native vtables loaded
                // for the process lifetime, including GC between retry attempts.
                LoadedLibraries.Add(helperPath, _helperDllPtr);
            }
        }
        var proc = NativeLibLoader.GetProc<CreateHelperDelegate>(_helperDllPtr, "CreateHelper");
        _helper = new ComWrapper<IIpcHelper>(proc());

        // instance of the main application - we should get it in the same thread
        using var instancesCom = _helper.InvokeAndWrap(helper => helper.GetRunningCamAppList(ref _executeContext));
        if (_executeContext.ResultStatus.Code == TResultStatusCode.rsError)
            throw new Exception(_executeContext.ResultStatus.Description);

        _application = instancesCom.InvokeAndWrap(instances =>
        {
            if (instances.Count == 0)
                throw new Exception("Can't get running instances");
            return instances.GetByProcessId(processId, _executeContext);
        });
        if (_executeContext.ResultStatus.Code == TResultStatusCode.rsError)
            throw new Exception(_executeContext.ResultStatus.Description);

        Logger.Debug("End of CAM helper initializing");
    }

    public void OpenProject(string projectPath)
    {
        Logger.Debug($"Open project, path - {projectPath}");
        _application?.Invoke(app =>
        {
            if (app is null)
                throw new Exception("Can't get application instance");
            app.OpenProject(projectPath, true, ref _executeContext);
            if (_executeContext.ResultStatus.Code != TResultStatusCode.rsSuccess)
                throw new Exception(_executeContext.ResultStatus.Description);
        });
    }

    public TreeNode Test(Action<string>? stageChanged = null, Action<TestStageResult>? stageCaptured = null,
        CancellationToken cancellationToken = default)
    {
        if (_application is null) throw new Exception("ApplicationCom is null");

        Logger.Debug("Start testing operations in CAM");

        using var activeProjectCom = _application.InvokeAndWrap(application => application.GetActiveProject(ref _executeContext));
        if (_executeContext.ResultStatus.Code == TResultStatusCode.rsError)
            throw new Exception(_executeContext.ResultStatus.Description);
        if (activeProjectCom == null)
            throw new Exception("Active project is not found");

        using var technologistCom = activeProjectCom.InvokeAndWrap(project => project.GetTechnologist(ref _executeContext));
        if (_executeContext.ResultStatus.Code == TResultStatusCode.rsError)
            throw new Exception(_executeContext.ResultStatus.Description);

        var stages = new List<TestStageResult>();
        void Capture(string name, TreeNode tree, bool complete, string? note = null)
        {
            var snapshot = new TestStageResult { Stage = name, Results = tree, IsComplete = complete, Note = note };
            stages.Add(snapshot);
            stageCaptured?.Invoke(snapshot);
        }
        cancellationToken.ThrowIfCancellationRequested();
        stageChanged?.Invoke("Calculate");
        technologyService.CalculateOperations(_application, technologistCom, _executeContext);

        var calculatedTree = GetOperationsTree(technologistCom);
        Capture("AfterCalculate", calculatedTree, AllEnabledLeaves(calculatedTree, n => n.IsCalculated));

        // Background collision status is a distinct validation phase. Material removal
        // readiness alone (IsMachiningResultCalculated) does not certify collision checks.
        if (_settings.ValidateBackgroundSimulation)
        {
            stageChanged?.Invoke("WaitForBackgroundSimulation");
            var watch = Stopwatch.StartNew();
            var backgroundTree = calculatedTree;
            string? lastErrorSnapshot = null;
            while (!AllEnabledLeaves(backgroundTree, n => n.IsCalculated && n.IsSimulated) &&
                   watch.Elapsed.TotalSeconds < Math.Max(1, _settings.BackgroundSimulationTimeoutInSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cancellationToken.WaitHandle.WaitOne(500)) cancellationToken.ThrowIfCancellationRequested();
                backgroundTree = GetOperationsTree(technologistCom);
                // Persist observed errors before any subsequent reset, including incomplete phases.
                if (StageResultPolicy.HasObservedErrors([new TestStageResult { Stage = "BackgroundSimulation", Results = backgroundTree }]))
                {
                    var signature = System.Text.Json.JsonSerializer.Serialize(backgroundTree);
                    if (signature != lastErrorSnapshot)
                        Capture("BackgroundSimulation", backgroundTree, false, "Errors observed while background checks were running.");
                    lastErrorSnapshot = signature;
                }
            }
            var complete = AllEnabledLeaves(backgroundTree, n => n.IsCalculated && n.IsSimulated);
            Capture("BackgroundSimulation", backgroundTree, complete, complete ? null :
                "Background collision validation did not finish. Enable background modeling and Machining > Collision detection > Machine in CAM settings. The foreground result alone cannot certify this phase.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        stageChanged?.Invoke("Simulate");
        simulationService.RunSimulation(_application, activeProjectCom, _executeContext);

        // get results
        stageChanged?.Invoke("ReadResults");
        var resultTree = GetOperationsTree(technologistCom);
        Capture("ForegroundSimulation", resultTree, AllEnabledLeaves(resultTree, n => n.IsCalculated && n.IsSimulated));

        Logger.Debug("End of testing operations in CAM");

        return StageResultPolicy.MergeObservedErrors(resultTree, stages);
    }

    private TreeNode GetOperationsTree(ComWrapper<ICamIpcTechnologist>? technologistCom)
    {
        var operationsTree = new TreeNode();

        technologistCom?.Invoke(technologist =>
        {
            Logger.Debug("Get operations tree");
            if (technologist == null)
                throw new Exception("Can't get technologist instance");

            var operations = ExecuteApiMethod(() => technologist.GetOperations(TCamApiReorderingMode.rmDesigned, _executeContext),
                                              "ICamIpcTechnologist.GetOperations");
            try { operationsTree = BuildOperationsTree(operations); }
            finally { ReleaseGetter(operations); }
        });

        return operationsTree;
    }

    public bool AreAllOperationsFastSimulated(TreeNode node)
    {
        if (node.Children.Count == 0)
        {
            return node.IsMachiningResultCalculated;
        }

        foreach (var child in node.Children)
        {
            if (!AreAllOperationsFastSimulated(child)) return false;
        }

        return true;
    }

    private static bool AllEnabledLeaves(TreeNode node, Func<TreeNode, bool> predicate)
    {
        if (!node.IsEnabled) return true;
        return node.Children.Count == 0 ? predicate(node) : node.Children.All(child => AllEnabledLeaves(child, predicate));
    }

    private TreeNode BuildOperationsTree(ICamIpcTechOperationIterator iterator)
    {
        Logger.Debug("Build operations tree");
        ExecuteVoidApiMethod(() => iterator.Reset(_executeContext), "ICamIpcTechOperationIterator.Reset");
        var rootOperation = ExecuteApiMethod(() => iterator.Current(_executeContext), "ICamIpcTechOperationIterator.Current");
        var root = new TreeNode();
        try { FillNode(rootOperation, root); }
        finally { ReleaseGetter(rootOperation); }
        var currentNode = root;

        while (true)
        {
            if (ExecuteApiMethod(() => iterator.MoveToChild(_executeContext), "ICamIpcTechOperationIterator.MoveToChild"))
            {
                var childOperation = ExecuteApiMethod(() => iterator.Current(_executeContext), "ICamIpcTechOperationIterator.Current");
                var child = new TreeNode();
                try { FillNode(childOperation, child); }
                finally { ReleaseGetter(childOperation); }
                currentNode.AddChild(child);
                currentNode = child;
            }
            else
            {
                while (!ExecuteApiMethod(() => iterator.MoveToSibling(_executeContext), "ICamIpcTechOperationIterator.MoveToSibling"))
                {
                    if (!ExecuteApiMethod(() => iterator.MoveToParent(_executeContext),
                                          "ICamIpcTechOperationIterator.MoveToParent"))
                    {
                        Logger.Debug("Operations tree has been built");
                        return root;
                    }
                    currentNode = currentNode.Parent;
                }

                var siblingOperation = ExecuteApiMethod(() => iterator.Current(_executeContext), "ICamIpcTechOperationIterator.Current");
                var sibling = new TreeNode();
                try { FillNode(siblingOperation, sibling); }
                finally { ReleaseGetter(siblingOperation); }
                currentNode.Parent.AddChild(sibling);
                currentNode = sibling;
            }
        }
    }

    private static void ReleaseGetter(object? value)
    {
        // Only fresh getter results from this traversal are released; callback or
        // wrapper-owned RCWs are never passed here. FinalReleaseComObject is unsafe.
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private void FillNode(ICamIpcTechOperation operation, TreeNode node)
    {
        Logger.Debug("Fill operation node");
        node.Name = ExecuteApiMethod(() => operation.GetName(_executeContext), "ICamIpcTechOperation.GetName");
        node.OperationId = ExecuteApiMethod(() => operation.GetId(_executeContext), "ICamIpcTechOperation.GetId");
        node.IsEnabled = ExecuteApiMethod(() => operation.GetEnabled(_executeContext), "ICamIpcTechOperation.GetEnabled");
        node.IsCalculated = ExecuteApiMethod(() => operation.GetCalculated(_executeContext), "ICamIpcTechOperation.GetCalculated");
        node.IsCollisionError = ExecuteApiMethod(() => operation.GetIsCollisionError(_executeContext), "ICamIpcTechOperation.GetIsCollisionError");
        node.IsCompensationError = ExecuteApiMethod(() => operation.GetIsCompensationError(_executeContext), "ICamIpcTechOperation.GetIsCompensationError");
        node.IsError = ExecuteApiMethod(() => operation.GetIsError(_executeContext), "ICamIpcTechOperation.GetIsError");
        node.IsGougeError = ExecuteApiMethod(() => operation.GetIsGougeError(_executeContext), "ICamIpcTechOperation.GetIsGougeError");
        node.IsHolderError = ExecuteApiMethod(() => operation.GetIsHolderError(_executeContext), "ICamIpcTechOperation.GetIsHolderError");
        node.IsMachiningResultCalculated = ExecuteApiMethod(() => operation.GetIsMachiningResultCalculated(_executeContext), "ICamIpcTechOperation.GetIsMachiningResultCalculated");
        node.IsPlungeError = ExecuteApiMethod(() => operation.GetIsPlungeError(_executeContext), "ICamIpcTechOperation.GetIsPlungeError");
        node.IsRapidError = ExecuteApiMethod(() => operation.GetIsRapidError(_executeContext), "ICamIpcTechOperation.GetIsRapidError");
        node.IsSimulated = ExecuteApiMethod(() => operation.GetSimulated(_executeContext), "ICamIpcTechOperation.GetSimulated");
        node.IsToolOverloadError = ExecuteApiMethod(() => operation.GetIsToolOverloadError(_executeContext), "ICamIpcTechOperation.GetIsToolOverloadError");
        node.IsTravelError = ExecuteApiMethod(() => operation.GetIsTravelError(_executeContext), "ICamIpcTechOperation.GetIsTravelError");
        node.IsTurnDirectionError = ExecuteApiMethod(() => operation.GetIsTurnDirectionError(_executeContext), "ICamIpcTechOperation.GetIsTurnDirectionError");
        Logger.Debug($"Node {node.Name} is filled");
    }

    private void ExecuteVoidApiMethod(Action apiMethod, string methodName)
    {
        apiMethod();
        if (_executeContext.ResultStatus.Code is TResultStatusCode.rsSuccess) return;

        Logger.Error($"{methodName} code - {_executeContext.ResultStatus.Code}");
        Logger.Error($"{methodName} description - {_executeContext.ResultStatus.Description}");
        throw new Exception(_executeContext.ResultStatus.Description);
    }

    private TValue ExecuteApiMethod<TValue>(Func<TValue> operation, string methodName)
    {
        var result = operation();
        if (_executeContext.ResultStatus.Code is TResultStatusCode.rsSuccess) return result;

        Logger.Error($"{methodName} code - {_executeContext.ResultStatus.Code}");
        Logger.Error($"{methodName} description - {_executeContext.ResultStatus.Description}");
        throw new Exception(_executeContext.ResultStatus.Description);
    }


    public void Dispose()
    {
        try
        {
            Logger.Info("Release COM resources");
            _application?.Dispose();
            _applicationTest?.Dispose();
            _helper?.Dispose();
            Logger.Info("End of releasing COM resources");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to release COM resources");
        }
    }
}
