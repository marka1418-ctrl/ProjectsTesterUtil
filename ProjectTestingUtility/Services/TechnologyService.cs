using CAMAPI.Application;
using CAMAPI.ApplicationMainForm;
using CAMAPI.DotnetHelper;
using CAMAPI.ResultStatus;
using CAMIPC.Application;
using CAMIPC.ExecuteContext;
using CAMIPC.Technologist;
using NLog;

namespace ProjectTestingUtility.Services;

public class TechnologyService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public void CalculateOperations(ComWrapper<ICamIpcApplication> applicationCom, ComWrapper<ICamIpcTechnologist> technologistCom, TExecuteContext executeContext)
    {
        // The CAM IPC helper normally registers this handle for us. Older SDK
        // builds require an explicit registration, so accept the SDK's
        // already-registered response and only unregister a handle we own.
        const int calculationThreadHandle = 1;
        var calculationThreadOwned = false;
        applicationCom.Invoke(application =>
        {
            try
            {
                application.RegisterWorkingThread(calculationThreadHandle, out var status);
                if (status.Code == TResultStatusCode.rsError)
                {
                    if (!status.Description.Contains("already registered", StringComparison.OrdinalIgnoreCase))
                        throw new Exception(status.Description);
                    Logger.Debug($"ENCY working thread {calculationThreadHandle} is already registered; reusing it");
                }
                else
                {
                    calculationThreadOwned = true;
                }
            }
            catch (Exception exception) when (exception.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase))
            {
                // Some ENCY builds report this as an IPC exception instead of
                // returning rsError. The worker is already usable in that case.
                Logger.Debug($"ENCY working thread {calculationThreadHandle} is already registered; reusing it");
            }
        });

        try
        {
        Logger.Debug("Switch to machining tab");
        applicationCom.Invoke(application =>
        {
            application.SetMainWorkMode(TMainWorkMode.mwmMachining, ref executeContext);
            if (executeContext.ResultStatus.Code == TResultStatusCode.rsError)
                throw new Exception(executeContext.ResultStatus.Description);
        });

        // calculate entire technology
        Logger.Debug("Get root operation");
        using var rootOperationCom = technologistCom.InvokeAndWrap(technologist => technologist.RootOperation);

        technologistCom.Invoke(technologist =>
        {
            Logger.Debug("Set current operation to root");

            rootOperationCom.Invoke(rootOperation =>
                technologist.SetCurrentOperation(rootOperation, ref executeContext));

            Logger.Debug("Reset all operations toolpath");
            technologist.ResetAllOperationsToolpath(ref executeContext);
            if (executeContext.ResultStatus.Code == TResultStatusCode.rsError)
                throw new Exception(executeContext.ResultStatus.Description);

            // ENCY's main thread must remain available to dispatch frames needed by
            // machine CLData. The SDK's calculation helper uses worker handle 1;
            // executing this blocking command on handle 0 can deadlock that dispatch.
            // Keep UI selection/reset on the caller's context and use a separate
            // context only for calculation, including inter-operation links.
            var calculationContext = executeContext;
            calculationContext.ExecuteSettings.ThreadHandle = calculationThreadHandle;
            Logger.Debug("Calculate all operations toolpath on ENCY worker thread");
            technologist.CalculateAllOperationsToolpath(true, ref calculationContext);
            if (calculationContext.ResultStatus.Code == TResultStatusCode.rsError)
                throw new Exception(calculationContext.ResultStatus.Description);
        });
        }
        finally
        {
            if (calculationThreadOwned)
            {
                applicationCom.Invoke(application =>
                {
                    application.UnregisterWorkingThread(calculationThreadHandle, out var status);
                    if (status.Code == TResultStatusCode.rsError)
                        Logger.Warn($"Failed to unregister ENCY working thread {calculationThreadHandle}: {status.Description}");
                });
            }
        }
    }
}

