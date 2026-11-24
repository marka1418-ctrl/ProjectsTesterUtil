namespace CAMAPI.ResultStatus
{
    public enum TResultStatusCode { rsSuccess, rsError }
    public sealed class ResultStatus
    {
        public TResultStatusCode Code { get; set; }
        public string Description { get; set; } = "";
    }
}
namespace CAMIPC.ExecuteContext
{
    public sealed class TExecuteContext
    {
        public CAMAPI.ResultStatus.ResultStatus ResultStatus { get; } = new();
    }
}
namespace CAMAPI.DotnetHelper
{
    public sealed class ComWrapper<T>(T instance) : IDisposable where T : class
    {
        public TResult Invoke<TResult>(Func<T, TResult> action) => action(instance);
        public void Invoke(Action<T> action) => action(instance);
        public ComWrapper<TResult>? InvokeAndWrap<TResult>(Func<T, TResult?> action) where TResult : class
        {
            var result = action(instance);
            return result == null ? null : new ComWrapper<TResult>(result);
        }
        public void Dispose() { }
    }
}
namespace CAMIPC.Application
{
    public interface ICamIpcApplication
    {
        int ProcessId { get; }
        FakeMainForm? MainForm { get; }
    }
    public sealed class FakeApplication : ICamIpcApplication
    {
        // This synthetic PID cannot match a live Windows process.
        public int ProcessId => int.MaxValue;
        public FakeMainForm? MainForm { get; init; }
    }
    public sealed class FakeMainForm
    {
        public bool Requested { get; private set; }
        public bool Reject { get; init; }
        public void SetCurrentInPrime(bool current)
        {
            if (Reject) throw new InvalidOperationException("Synthetic SDK failure.");
            Requested = current;
        }
        public long GetMainWindowHandle(ref CAMIPC.ExecuteContext.TExecuteContext context) => 0;
    }
}
namespace CAMIPC.Helper
{
    public interface IIpcHelper
    {
        FakePrime? GetPrime(int pid, ref CAMIPC.ExecuteContext.TExecuteContext context);
    }
    public sealed class FakePrime
    {
        public bool ApplicationBelongs(int pid, ref CAMIPC.ExecuteContext.TExecuteContext context) => false;
        public FakeApplicationList GetApplicationsList(ref CAMIPC.ExecuteContext.TExecuteContext context) => new();
    }
    public sealed class FakeApplicationList
    {
        public int Count() => 0;
        public int Get(int index) => throw new IndexOutOfRangeException();
    }
}
