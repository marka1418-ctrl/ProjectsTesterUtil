namespace CAMAPI.Application { }

namespace CAMAPI.ApplicationMainForm
{
    public enum TMainWorkMode { mwmMachining }
}

namespace CAMAPI.ResultStatus
{
    public enum TResultStatusCode { rsSuccess, rsError }
    public struct TResultStatus
    {
        public TResultStatusCode Code;
        public string? Description;
    }
}

namespace CAMIPC.ExecuteContext
{
    // Both types are value types in the actual SDK metadata.
    public struct TExecuteSettings
    {
        public int ThreadHandle;
        public int Timeout;
    }
    public struct TExecuteContext
    {
        public TExecuteSettings ExecuteSettings;
        public CAMAPI.ResultStatus.TResultStatus ResultStatus;
    }
}

namespace CAMAPI.DotnetHelper
{
    public sealed class ComWrapper<T>(T value) : IDisposable
    {
        public void Invoke(Action<T> call) => call(value);
        public ComWrapper<TResult> InvokeAndWrap<TResult>(Func<T, TResult> call) => new(call(value));
        public void Dispose() { }
    }
}

namespace CAMIPC.Application
{
    public interface ICamIpcApplication
    {
        void RegisterWorkingThread(int threadHandle, out CAMAPI.ResultStatus.TResultStatus status);
        void UnregisterWorkingThread(int threadHandle, out CAMAPI.ResultStatus.TResultStatus status);
        void SetMainWorkMode(CAMAPI.ApplicationMainForm.TMainWorkMode mode,
            ref CAMIPC.ExecuteContext.TExecuteContext context);
    }
}

namespace CAMIPC.Technologist
{
    public sealed class Operation;
    public interface ICamIpcTechnologist
    {
        Operation RootOperation { get; }
        void SetCurrentOperation(Operation operation, ref CAMIPC.ExecuteContext.TExecuteContext context);
        void ResetAllOperationsToolpath(ref CAMIPC.ExecuteContext.TExecuteContext context);
        void CalculateAllOperationsToolpath(bool calcLinksBetweenOperations,
            ref CAMIPC.ExecuteContext.TExecuteContext context);
    }
}

namespace NLog
{
    public sealed class Logger { public void Debug(string message) { } public void Warn(string message) { } }
    public static class LogManager { public static Logger GetCurrentClassLogger() => new(); }
}
