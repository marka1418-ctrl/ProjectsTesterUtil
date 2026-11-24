using CAMAPI.Application;
using CAMAPI.ApplicationMainForm;
using CAMAPI.DotnetHelper;
using CAMAPI.ResultStatus;
using CAMIPC.Application;
using CAMIPC.ExecuteContext;
using CAMIPC.Project;
using NLog;
using ProjectTestingUtility.Models;

namespace ProjectTestingUtility.Services;

public class SimulationService(SettingsService settingsService)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Settings _settings = settingsService.Settings;
    
    public void RunSimulation(ComWrapper<ICamIpcApplication> applicationCom, ComWrapper<ICamIpcProject> activeProjectCom, TExecuteContext executeContext)
    {        
        Logger.Debug("Switch to simulation tab");
        applicationCom.Invoke(application =>
        {
            application.SetMainWorkMode(TMainWorkMode.mwmSimulating, ref executeContext);
            if (executeContext.ResultStatus.Code == TResultStatusCode.rsError)
                throw new Exception(executeContext.ResultStatus.Description);
        });
        
        // arrange
        using var simulatorCom = activeProjectCom.InvokeAndWrap(project => project.Simulator);
        
        Logger.Debug("Run simulation");
        simulatorCom.Invoke(simulator =>
        {
            // setup simulation parameters
            simulator.BreakOnStopCommand = _settings.BreakOnStopCommand;
            simulator.BreakOnEndOfOperation = _settings.BreakOnEndOfOperation;
            simulator.BreakOnErrors = _settings.BreakOnErrors;

            simulator.CheckGouges = _settings.CheckGouges;
            simulator.CheckHolderCollisions = _settings.CheckHolderCollisions;
            simulator.CheckMachineCollisions = _settings.CheckMachineCollisions;
            simulator.SimulationSpeedPercent = _settings.SimulationSpeedPercent;
            Logger.Info($"Effective simulator settings: Machine={simulator.CheckMachineCollisions}, Holder={simulator.CheckHolderCollisions}, Gouges={simulator.CheckGouges}, Hidden={simulator.CheckHiddenNodesCollisions}, MultiChannel={simulator.MultiChannelMode}, Method={simulator.SimulationMethod}, Smooth={simulator.SmoothSimulationEnabled}");

            // run simulation
            Logger.Debug("Reset simulation results");
            simulator.ResetSimulationResults();

            Logger.Debug($"Smooth simulation start, speed percent - {simulator.SimulationSpeedPercent}");
            simulator.SmoothSimulationStart();
        });

        Thread.Sleep(1000); //wait for CAM tree updated
    }
}
