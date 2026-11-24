using CAMAPI.ApplicationMainForm;
using CAMAPI.DotnetHelper;
using CAMAPI.ResultStatus;
using CAMIPC.Application;
using CAMIPC.ExecuteContext;
using CAMIPC.Technologist;
using ProjectTestingUtility.Services;

var service = new TechnologyService();
var callerContext = new TExecuteContext
{
    ExecuteSettings = new() { ThreadHandle = 0, Timeout = 123456 },
    ResultStatus = new() { Code = TResultStatusCode.rsSuccess, Description = "caller status" }
};
var success = new Scenario();
Run(success);
Require(success.Events.SequenceEqual(new[] { "machining", "root", "select-root", "reset", "calculate" }),
    "The complete project is selected and reset before calculation.");
Require(success.CalculatedOperations == 9,
    "All previously calculated operation paths were discarded and recalculated on an ENCY worker.");
Require(success.RegisteredThreads.SequenceEqual(new[] { 1 }) && success.UnregisteredThreads.SequenceEqual(new[] { 1 }),
    "The ENCY calculation worker is registered and unregistered on the same IPC thread.");
Require(callerContext.ExecuteSettings.ThreadHandle == 0 && callerContext.ExecuteSettings.Timeout == 123456 &&
        callerContext.ResultStatus.Description == "caller status",
    "The caller's UI context, timeout and status are preserved.");

var calculationFailure = new Scenario { CalculationError = "machine CLData calculation failed" };
ExpectFailure(calculationFailure, "machine CLData calculation failed");
Require(calculationFailure.Events.Last() == "calculate", "Calculation reached the native API before its error was reported.");

var resetFailure = new Scenario { ResetError = "could not reset trajectories" };
ExpectFailure(resetFailure, "could not reset trajectories");
Require(!resetFailure.Events.Contains("calculate"), "Reset errors prevent calculation of stale trajectories.");

var modeFailure = new Scenario { ModeError = "could not enter machining" };
ExpectFailure(modeFailure, "could not enter machining");
Require(modeFailure.Events.SequenceEqual(new[] { "machining" }), "A failed mode change prevents any technology changes.");

Console.WriteLine("PASS: worker dispatch, complete reset, links, context isolation, and native failure propagation.");

void Run(Scenario scenario)
{
    using var app = new ComWrapper<ICamIpcApplication>(scenario);
    using var technologist = new ComWrapper<ICamIpcTechnologist>(scenario);
    service.CalculateOperations(app, technologist, callerContext);
}

void ExpectFailure(Scenario scenario, string message)
{
    try { Run(scenario); }
    catch (Exception exception) when (exception.Message == message) { return; }
    throw new Exception("Expected native failure: " + message);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class Scenario : ICamIpcApplication, ICamIpcTechnologist
{
    private readonly Operation root = new();
    private bool rootSelected;
    private bool wasReset;
    public List<string> Events { get; } = [];
    public int CalculatedOperations { get; private set; } = 9;
    public List<int> RegisteredThreads { get; } = [];
    public List<int> UnregisteredThreads { get; } = [];
    public string? ModeError { get; init; }
    public string? ResetError { get; init; }
    public string? CalculationError { get; init; }

    public void SetMainWorkMode(TMainWorkMode mode, ref TExecuteContext context)
    {
        CheckUiContext(context);
        if (mode != TMainWorkMode.mwmMachining) throw new Exception("Expected machining mode.");
        Events.Add("machining");
        SetStatus(ref context, ModeError);
    }

    public void RegisterWorkingThread(int threadHandle, out TResultStatus status)
    {
        if (threadHandle != 1) throw new Exception("Expected the ENCY calculation worker handle.");
        RegisteredThreads.Add(threadHandle);
        SetStatus(out status, null);
    }

    public void UnregisterWorkingThread(int threadHandle, out TResultStatus status)
    {
        if (threadHandle != 1) throw new Exception("Expected the ENCY calculation worker handle.");
        UnregisteredThreads.Add(threadHandle);
        SetStatus(out status, null);
    }

    public Operation RootOperation
    {
        get { Events.Add("root"); return root; }
    }

    public void SetCurrentOperation(Operation operation, ref TExecuteContext context)
    {
        CheckUiContext(context);
        if (!ReferenceEquals(operation, root)) throw new Exception("Expected the whole project root.");
        rootSelected = true;
        Events.Add("select-root");
        SetStatus(ref context, null);
    }

    public void ResetAllOperationsToolpath(ref TExecuteContext context)
    {
        CheckUiContext(context);
        if (!rootSelected) throw new Exception("Reset must target the complete technology.");
        Events.Add("reset");
        if (ResetError is null) { CalculatedOperations = 0; wasReset = true; }
        SetStatus(ref context, ResetError);
    }

    public void CalculateAllOperationsToolpath(bool calcLinksBetweenOperations, ref TExecuteContext context)
    {
        // The reproduced ENCY 301 failure blocks frame dispatch when calculation
        // runs on the main thread. Rejecting that dispatch makes this regression
        // fail immediately instead of waiting for a native test timeout.
        if (context.ExecuteSettings.ThreadHandle != 1)
            throw new Exception("Calculation blocks frame dispatch on ENCY's main thread.");
        if (context.ExecuteSettings.Timeout != 123456)
            throw new Exception("Worker dispatch changed the caller's timeout policy.");
        if (!calcLinksBetweenOperations)
            throw new Exception("Inter-operation links must remain part of the calculation.");
        if (!wasReset || CalculatedOperations != 0)
            throw new Exception("Saved toolpaths must be reset before recalculation.");
        Events.Add("calculate");
        if (CalculationError is null) CalculatedOperations = 9;
        SetStatus(ref context, CalculationError);
    }

    private static void CheckUiContext(TExecuteContext context)
    {
        if (context.ExecuteSettings.ThreadHandle != 0)
            throw new Exception("UI selection/reset must retain the main-thread context.");
    }

    private static void SetStatus(ref TExecuteContext context, string? error) => context.ResultStatus = new()
    {
        Code = error is null ? TResultStatusCode.rsSuccess : TResultStatusCode.rsError,
        Description = error
    };

    private static void SetStatus(out TResultStatus status, string? error) => status = new()
    {
        Code = error is null ? TResultStatusCode.rsSuccess : TResultStatusCode.rsError,
        Description = error
    };
}
