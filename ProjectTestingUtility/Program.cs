using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using NLog;
using ProjectTestingUtility.CAMConnector;
using ProjectTestingUtility.Services;

namespace ProjectTestingUtility;

public class Program
{
    private static async Task Main(string[] args)
    {
        var runnerStarted = false;
        try
        {
            LoggerInitializer.Configure();
            RunStatusReporter.Write("Starting", "Settings", "Loading test settings.");

            var serviceProvider = new ServiceCollection().AddHttpClient()
                                                         .AddSingleton<AppRunner>()
                                                         .AddSingleton<SettingsService>()
                                                         .AddSingleton<WebService>()
                                                         .AddSingleton<AuthorizationService>()
                                                         .AddSingleton<CAMLMConnector>()
                                                         .AddSingleton<SimulationService>()
                                                         .AddSingleton<TechnologyService>()
                                                         .BuildServiceProvider();

            LogManager.GetCurrentClassLogger().Debug("App is running");
            serviceProvider.GetRequiredService<SettingsService>().Initialize();
            var appRunner = serviceProvider.GetRequiredService<AppRunner>();
            runnerStarted = true;
            await appRunner.Start(args);
            await appRunner.Stop(0);
        }
        catch (Exception e)
        { 
            LogManager.GetCurrentClassLogger().Error(e, "Unhandled exception: ");
            if (!runnerStarted)
                RunStatusReporter.Write("Failed", "Startup", RunStatusReporter.FailureMessage(e, "Startup"));
            Environment.ExitCode = 1;
        }
        finally
        {
            LogManager.Shutdown();
        }
        // All reports and recordings have been finalized. SDK-owned foreground
        // threads must not keep this command-line worker alive after the run.
        Environment.Exit(Environment.ExitCode);
    }
}
