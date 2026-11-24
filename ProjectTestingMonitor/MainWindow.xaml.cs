using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace ProjectTesterMonitor
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        class AppSettings
        {
           public string SelectedCAMPath { get; set; }
            public bool SystemUsageMonitoringCpu { get; set; } = false; 
            public bool SystemUsageMonitoringRam { get; set; } = false; 
        };

        private static readonly string WORK_DIR = AppPaths.Root;

        private static readonly string SETTINGS_FN = Path.Combine(WORK_DIR, "MonitorSettings.json");

        private static readonly string GOODS_FOLDER = "goods";
        private static readonly string CALC_FAIL_FOLDER = "calc_fail";
        private static readonly string SIM_FAIL_FOLDER = "sim_fail";

        private string CAM_EXE_PATH = @"C:\Program Files\ENCY Software\ENCY 3\Bin64\ency.exe";

        private bool SYSTEM_USAGE_MONITORING_CPU = false;
        private bool SYSTEM_USAGE_MONITORING_RAM = false;

        private readonly object _countersLock = new object();
        int checked_couner = 0;
        int calc_fail_counter = 0;
        int sim_fail_counter = 0;
        int good_counter = 0;

        int lastId = int.MaxValue;


        PerformanceCounter? ramAvailable = null;
        PerformanceCounter? cpuCounter = null;
        int cpuUsage = 0;
        int ramUsage = 0;
        private float? cachedRamTotal = null; // Cache total RAM to avoid WMI calls
        private bool ramTotalInitialized = false; // Track if we've tried to initialize
        private bool ramCounterInitializing = false; // Track if RAM counter is being initialized
        private bool cpuCounterInitializing = false; // Track if CPU counter is being initialized

        private TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
        private DispatcherTimer? uiUpdateTimer = null;
        private string monitoringCurDir = "";
        // Процессы, которые нужно убивать (имена без .exe)
        private string[] ToKill = new[] { "SC", "ency", "sc_prime", "ency_prime" };

        private CancellationTokenSource _cts = new CancellationTokenSource();

        private int lastCheckedProject = int.MaxValue;
        private int sameProjectFailCounter = 0;

        private async Task MonitorLoop(string utilityProcName)
        {
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool running = IsProcessRunning(utilityProcName);
                    // A finished run must stop the monitor even if native teardown
                    // keeps the worker process alive for a while.
                    var runStatus = TesterRunStatus.Read(Path.Combine(WORK_DIR, "run-status.json"));
                    if (!running || runStatus is { IsTerminal: true })
                    {
                        if (runStatus is { IsTerminal: true })
                        {
                            RestoreStoppedControls();
                            var failed = runStatus.State == "Failed";
                            var message = string.IsNullOrWhiteSpace(runStatus.Message)
                                ? (failed ? "Testing stopped before it could finish." : "Testing completed. No more projects match the selected filters.")
                                : runStatus.Message;
                            if (failed)
                                message += $"\n\nStage: {runStatus.Stage}\nLog: {Path.Combine(WORK_DIR, "logs", "log.log")}";
                            writeLog($"Tester {runStatus.State}, stage {runStatus.Stage}: {runStatus.Message}");
                            MessageBox.Show(this, message, failed ? "Testing could not continue" : "Testing completed",
                                MessageBoxButton.OK, failed ? MessageBoxImage.Error : MessageBoxImage.Information);
                            break;
                        }
                        writeLog("ProjectTestingUtility not running, restarting...");

                        // check lastId
                        var checkedProject = int.MaxValue;
                        var checkedFiles = System.IO.Directory.GetFiles(System.IO.Path.Combine(WORK_DIR, "results"), "*.json", SearchOption.AllDirectories);
                        foreach (var file in checkedFiles)
                        {
                            try
                            {
                                var id = int.Parse(System.IO.Path.GetFileNameWithoutExtension(file));
                                if (id < checkedProject)
                                {
                                    checkedProject = id;
                                }
                            }
                            catch (Exception)
                            {

                            }
                        }
                        if (checkedProject == lastCheckedProject)
                        {
                            sameProjectFailCounter++;
                        }
                        else
                        {
                            sameProjectFailCounter = 0;
                        }
                        lastCheckedProject = checkedProject;

                        if (sameProjectFailCounter > 10)
                        {
                            writeLog("The same project is failing repeatedly, stopping the monitor.");
                            RestoreStoppedControls();
                            MessageBox.Show(this, "The testing utility stopped repeatedly without completing a project. Check the tester log and Settings before running again.",
                                "Testing stopped", MessageBoxButton.OK, MessageBoxImage.Error);
                            break;
                        }


                        bool started = StartUtility(restartCam: true);
                        if (started)
                            writeLog("ProjectTestingUtility started.");
                        else
                        {
                            writeLog("Failed to start ProjectTestingUtility.");
                            break;
                        }
                    }
                    else
                    {
                    }
                }
                catch (Exception ex)
                {
                    writeLog("Fail in monitoring loop: " + ex.Message);
                    RestoreStoppedControls();
                    MessageBox.Show(this, "Monitoring stopped: " + ex.Message, "Testing stopped",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                }

                try
                {
                    await Task.Delay(CheckInterval, token);
                }
                catch (TaskCanceledException) { break; }
            }
        }

        static bool IsProcessRunning(string processNameWithoutExt)
        {
            if (string.IsNullOrWhiteSpace(processNameWithoutExt))
                return false;

            // Пытаемся найти процессы по имени (без .exe)
            var procs = Process.GetProcessesByName(processNameWithoutExt);
            return procs != null && procs.Length > 0;
        }

        private void KillProcesses(string[] namesWithoutExt)
        {
            foreach (var name in namesWithoutExt)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    if (procs.Length == 0)
                    {
                        continue;
                    }

                    foreach (var p in procs)
                    {
                        try
                        {
                            writeLog($"Killing PID={p.Id} ({p.ProcessName}) ...");
                            if (!p.HasExited)
                            {
                                try
                                {
                                    p.CloseMainWindow();
                                    if (!p.WaitForExit(2000))
                                    {
                                        p.Kill(entireProcessTree: true);
                                        p.WaitForExit(5000);
                                    }
                                }
                                catch
                                {
                                    if (!p.HasExited)
                                    {
                                        p.Kill(entireProcessTree: true);
                                        p.WaitForExit(5000);
                                    }
                                }
                            }
                            writeLog($"Killed PID={p.Id} ({p.ProcessName})" );
                        }
                        catch (Exception exProc)
                        {
                            writeLog($"Failed to kill PID={p.Id}: {exProc.Message}");    
                        }
                    }
                }
                catch (Exception ex)
                {
                    writeLog($"Failed to get processes by name '{name}': {ex.Message}");
                }
            }
        }

        private bool StartUtility(bool restartCam = false, bool retryLastProject = false)
        {
            try
            {
                var settingsError = TesterRunStatus.GetSettingsError(Path.Combine(WORK_DIR, "settings.json"));
                if (settingsError != null)
                {
                    RestoreStoppedControls();
                    MessageBox.Show(this, settingsError, "Test settings are required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (!File.Exists(CAM_EXE_PATH))
                    throw new FileNotFoundException("The selected ENCY executable was not found. Select its location and try again.");
                var utilityPath = Path.Combine(WORK_DIR, "ProjectTestingUtility.exe");
                if (!File.Exists(utilityPath))
                    throw new FileNotFoundException("The testing utility was not found in the data folder. Restart the monitor to extract it again.");

                // Clear only the status of the preceding run before launching a fresh process.
                File.Delete(Path.Combine(WORK_DIR, "run-status.json"));

                // check lastId
                lastId = int.MaxValue;
                var checkedFiles = System.IO.Directory.GetFiles(System.IO.Path.Combine(WORK_DIR, "results"), "*.json", SearchOption.AllDirectories);
                foreach (var file in checkedFiles)
                {
                    try
                    {
                        var id = int.Parse(System.IO.Path.GetFileNameWithoutExtension(file));
                        if (id < lastId)
                        {
                            lastId = id;
                        }
                    }
                    catch (Exception)
                    {

                    }
                }
                lastId = TesterRunStatus.ResumeProjectId(lastId, retryLastProject);
                if (restartCam)
                    KillProcesses(ToKill);

                StopBtn.Visibility = Visibility.Visible;
                RunBtn.Visibility = Visibility.Collapsed;
                SettingsBtn.IsEnabled = false;
                SettingsBtn.ToolTip = "Settings unavailable while testing is running";
                // Path selector is always enabled

                uptimeStopWatch.Start();

                // start testing utility
                // check if the lastId is set
                var args = $"\"{CAM_EXE_PATH}\"";
                if (lastId != int.MaxValue)
                {
                    args += $" {lastId}";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = utilityPath,
                    Arguments = args,
                    WorkingDirectory = WORK_DIR,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                using var startedProcess = Process.Start(psi);
                if (startedProcess == null)
                    throw new InvalidOperationException("Windows did not start the testing utility.");

                return true;
            }
            catch (Exception ex)
            {
                writeLog("Failed to start utility: " + ex.Message);
                RestoreStoppedControls();
                MessageBox.Show(this, "Could not start testing: " + ex.Message, "Testing could not start",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void RestoreStoppedControls()
        {
            _cts.Cancel();
            StopBtn.Visibility = Visibility.Collapsed;
            RunBtn.Visibility = Visibility.Visible;
            SettingsBtn.IsEnabled = true;
            SettingsBtn.ToolTip = "Settings";
            uptimeStopWatch.Stop();
        }



        private void writeLog(string msg)
        {
            try
            {
                msg = System.DateTime.Now.ToString("[MM-dd-yyyy HH:mm:ss] ") + msg;
                var logFn = Path.Combine(WORK_DIR, "log.txt");
                
                // Limit log file size to prevent memory issues (keep last 1000 lines)
                if (File.Exists(logFn))
                {
                    var fileInfo = new FileInfo(logFn);
                    // If log file exceeds 5MB, truncate it
                    if (fileInfo.Length > 5 * 1024 * 1024)
                    {
                        var lines = File.ReadAllLines(logFn, Encoding.UTF8);
                        var recentLines = lines.Skip(Math.Max(0, lines.Length - 1000)).ToArray();
                        File.WriteAllLines(logFn, recentLines, Encoding.UTF8);
                    }
                }
                
                File.AppendAllText(logFn, msg + "\n", Encoding.UTF8);
            }
            catch
            {
                // Silently ignore logging errors to prevent crashes
            }
        }

        private bool saveSettings(string settingsFn, dynamic settings)
        {
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(settingsFn, json);
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        private dynamic loadSettings(string settingsFn)
        {
            try
            {
                var json = System.IO.File.ReadAllText(settingsFn);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(json);
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private float getTotalMemoryInMB()
        {
            // Return cached value if available
            if (cachedRamTotal.HasValue)
                return cachedRamTotal.Value;

            // Use WMI with timeout to avoid blocking
            float totalMb = 0;
            try
            {
                var task = Task.Run(() =>
                {
                    try
                    {
                        using (var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                        {
                            searcher.Options.Timeout = TimeSpan.FromSeconds(2); // Set timeout
                            foreach (var obj in searcher.Get())
                            {
                                if (obj["TotalPhysicalMemory"] != null)
                                {
                                    ulong totalBytes = (ulong)obj["TotalPhysicalMemory"];
                                    return totalBytes / 1024f / 1024f;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        writeLog("Error getting total RAM from WMI: " + ex.Message);
                    }
                    return 0f;
                });

                // Wait max 1 second for WMI query to avoid blocking UI
                if (task.Wait(1000) && task.Result > 0)
                {
                    cachedRamTotal = task.Result;
                    return cachedRamTotal.Value;
                }
            }
            catch (Exception ex)
            {
                writeLog("Error in getTotalMemoryInMB: " + ex.Message);
            }

            // Fallback: use a reasonable default if WMI fails or times out
            if (!cachedRamTotal.HasValue)
            {
                cachedRamTotal = 8192; // Default to 8GB if we can't determine
                writeLog("Using default RAM value: 8GB");
            }
            return cachedRamTotal.Value;
        }

        private void calcCpuAndRamUsage()
        {
            // Process RAM monitoring independently - don't let errors stop CPU monitoring
            if (SYSTEM_USAGE_MONITORING_RAM)
            {
                try
                {
                    // Lazy initialization of PerformanceCounter - do it in background to avoid blocking
                    if (ramAvailable == null && !ramCounterInitializing)
                    {
                        // Start initialization in background - don't wait for it
                        ramCounterInitializing = true;
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                var counter = new PerformanceCounter("Memory", "Available MBytes");
                                // First call to NextValue() returns 0, so call it twice
                                counter.NextValue();
                                System.Threading.Thread.Sleep(100); // Small delay for accurate first reading
                                ramAvailable = counter;
                                ramCounterInitializing = false;
                                writeLog("RAM counter initialized successfully");
                            }
                            catch (Exception initEx)
                            {
                                writeLog("Error initializing RAM counter: " + initEx.Message);
                                ramAvailable = null;
                                ramCounterInitializing = false;
                            }
                        });
                        // Don't wait - continue to CPU monitoring even if RAM is initializing
                    }

                    // Only proceed if counter was successfully initialized
                    if (ramAvailable != null)
                    {
                        // Initialize total RAM only once in background to avoid blocking
                        if (!ramTotalInitialized)
                        {
                            ramTotalInitialized = true;
                            // Initialize in background task to avoid blocking UI
                            _ = Task.Run(() => getTotalMemoryInMB());
                        }

                        try
                        {
                            // Get total RAM (cached after first call)
                            var ramTotal = cachedRamTotal ?? 8192; // Use cached or default
                            float available = ramAvailable.NextValue();
                            
                            if (ramTotal > 0)
                            {
                                float used = ramTotal - available;
                                float percentUsed = (used / ramTotal) * 100;
                                int newRamUsage = Math.Max(0, Math.Min(100, (int)percentUsed)); // Clamp between 0-100
                                
                                // Always update the value, even if it's the same
                                ramUsage = newRamUsage;
                            }
                        }
                        catch (Exception readEx)
                        {
                            writeLog("Error reading RAM usage: " + readEx.Message);
                            // Reset counter on read error
                            try
                            {
                                ramAvailable?.Dispose();
                            }
                            catch { }
                            ramAvailable = null;
                            ramCounterInitializing = false;
                        }
                    }
                    // If counter is still initializing, values will be updated once initialization completes
                }
                catch (Exception ex)
                {
                    writeLog("Error in RAM monitoring: " + ex.Message);
                    // Reset counter on error to retry next time
                    try
                    {
                        ramAvailable?.Dispose();
                    }
                    catch { }
                    ramAvailable = null;
                }
            }
            else
            {
                // If monitoring is disabled, dispose counter if it exists
                if (ramAvailable != null)
                {
                    try
                    {
                        ramAvailable.Dispose();
                    }
                    catch { }
                    ramAvailable = null;
                }
            }

            // Process CPU monitoring independently - don't let errors stop the monitoring loop
            if (SYSTEM_USAGE_MONITORING_CPU)
            {
                try
                {
                    // Lazy initialization of PerformanceCounter - do it in background to avoid blocking
                    if (cpuCounter == null && !cpuCounterInitializing)
                    {
                        // Start initialization in background - don't wait for it
                        cpuCounterInitializing = true;
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                                // First call to NextValue() returns 0, so call it twice
                                counter.NextValue();
                                System.Threading.Thread.Sleep(100); // Small delay for accurate first reading
                                cpuCounter = counter;
                                cpuCounterInitializing = false;
                                writeLog("CPU counter initialized successfully");
                            }
                            catch (Exception initEx)
                            {
                                writeLog("Error initializing CPU counter: " + initEx.Message);
                                cpuCounter = null;
                                cpuCounterInitializing = false;
                            }
                        });
                        // Don't wait - initialization will complete in background
                    }

                    // Only proceed if counter was successfully initialized
                    if (cpuCounter != null)
                    {
                        try
                        {
                            float cpu = cpuCounter.NextValue();
                            int newCpuUsage = Math.Max(0, Math.Min(100, (int)Math.Round(cpu))); // Clamp between 0-100
                            
                            // Always update the value, even if it's the same
                            cpuUsage = newCpuUsage;
                            
                            // Log first few updates to verify it's working
                            if (cpuUsage > 0 && cpuUsage < 100)
                            {
                                // Values are updating correctly
                            }
                        }
                        catch (Exception readEx)
                        {
                            writeLog("Error reading CPU usage: " + readEx.Message);
                            // Reset counter on read error
                            try
                            {
                                cpuCounter?.Dispose();
                            }
                            catch { }
                            cpuCounter = null;
                            cpuCounterInitializing = false;
                        }
                    }
                    // If counter is still initializing, values will be updated once initialization completes
                }
                catch (Exception ex)
                {
                    writeLog("Error in CPU monitoring: " + ex.Message);
                    // Reset counter on error to retry next time
                    try
                    {
                        cpuCounter?.Dispose();
                    }
                    catch { }
                    cpuCounter = null;
                }
            }
            else
            {
                // If monitoring is disabled, dispose counter if it exists
                if (cpuCounter != null)
                {
                    try
                    {
                        cpuCounter.Dispose();
                    }
                    catch { }
                    cpuCounter = null;
                }
            }
        }


        private bool checkIsCalcErrorRecursive(dynamic json)
        {
            if (json == null)
            {
                return false;
            }

            bool calculated = json.IsCalculated;
            bool collisionError = json.IsCollisionError;
            bool compensationError = json.IsCompensationError;
            bool error = json.IsError;
            bool gougeError = json.IsGougeError;
            bool holderError = json.IsHolderError;
            bool machiningResultCalculated = json.IsMachiningResultCalculated;
            bool plungeError = json.IsPlungeError;
            bool rapidError = json.IsRapidError;
            bool isSimulated = json.IsSimulated;
            bool toolOverloadError = json.IsToolOverloadError;
            bool travelError = json.IsTravelError;
            bool turnDirectionError = json.IsTurnDirectionError;

            var calcError = !calculated;
            if (calcError)
            {
                return true;
            }
            else
            {
                // check children
                foreach (var child in json.Children)
                {
                    if (checkIsCalcErrorRecursive(child))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private void extractResource(string name, bool overwrite = true)
        {
            try
            {
                string resourceName = "ProjectTesterMonitor.Resources." + name;
                string outputPath = Path.Combine(WORK_DIR, name);
                
                // If file exists and is in use, skip extraction (it's already there)
                if (File.Exists(outputPath) && !overwrite)
                {
                    return;
                }
                
                // Check if file is in use by another process
                if (File.Exists(outputPath) && overwrite)
                {
                    try
                    {
                        // Try to open file for writing to check if it's locked
                        using (FileStream testStream = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.None))
                        {
                            // File is not locked, we can overwrite it
                        }
                    }
                    catch (IOException)
                    {
                        // File is locked by another process, skip extraction
                        writeLog($"File {name} is in use by another process, skipping extraction");
                        return;
                    }
                }
                
                if (overwrite || !File.Exists(outputPath))
                {
                    using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                    {
                        if (resource == null)
                        {
                            writeLog($"Resource {name} not found in assembly");
                            return;
                        }

                        using (FileStream file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            resource.CopyTo(file);
                        }
                    }
                }
            }
            catch (IOException ioEx)
            {
                // File is locked or other IO error - log and continue
                writeLog($"Error extracting {name}: {ioEx.Message}");
            }
            catch (Exception ex)
            {
                // Other errors - log and continue
                writeLog($"Error extracting {name}: {ex.Message}");
            }
        }

        private void extractResources()
        {
            writeLog("Extracting resources");
            extractResource("ProjectTestingUtility.exe", true);
            extractResource("settings.json", false);
            extractResource("settings_All_Public.json", false);
            extractResource("settings_Recommended.json", false);
            extractResource("settings_TestSuite.json", false);

        }

        private bool checkIsSimErrorRecursive(dynamic json)
        {
            if (json == null)
            {
                return false;
            }

            bool calculated = json.IsCalculated;
            bool collisionError = json.IsCollisionError;
            bool compensationError = json.IsCompensationError;
            bool error = json.IsError;
            bool gougeError = json.IsGougeError;
            bool holderError = json.IsHolderError;
            bool plungeError = json.IsPlungeError;
            bool rapidError = json.IsRapidError;
            bool isSimulated = json.IsSimulated;
            bool toolOverloadError = json.IsToolOverloadError;
            bool travelError = json.IsTravelError;
            bool turnDirectionError = json.IsTurnDirectionError;

            var simError = !isSimulated || error || collisionError || compensationError || gougeError || holderError || plungeError || rapidError || toolOverloadError || travelError || turnDirectionError;
            if (simError)
            {
                return true;
            }
            else
            {
                // check children
                foreach (var child in json.Children)
                {
                    if (checkIsSimErrorRecursive(child))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public int scan(string uncheckedsDir, string goodsDir, string calcFailDir, string simFailDir)
        {
            int filesProcessed = 0;
            
            // Check if directory exists
            if (!System.IO.Directory.Exists(uncheckedsDir))
            {
                return 0;
            }
            
            // find all json files in current directory and subdirectories (but exclude already processed folders)
            var files = System.IO.Directory.GetFiles(uncheckedsDir, "*.json", System.IO.SearchOption.TopDirectoryOnly);
            
            // Also check if there's an "unchecked" or root results folder
            if (files.Length == 0)
            {
                // Try to find files in subdirectories that might contain unchecked files
                var allFiles = System.IO.Directory.GetFiles(uncheckedsDir, "*.json", System.IO.SearchOption.AllDirectories);
                // Filter out files that are already in processed folders
                files = allFiles.Where(f => 
                    !f.Contains(GOODS_FOLDER) && 
                    !f.Contains(CALC_FAIL_FOLDER) && 
                    !f.Contains(SIM_FAIL_FOLDER)).ToArray();
            }
            
            // open each file and check
            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(json);
                    if (obj == null)
                    {
                        continue;
                    }
                    var classification = ResultClassification.Read(Newtonsoft.Json.Linq.JObject.Parse(json));

                    lock (_countersLock)
                    {
                        if (classification.Category == "CalcFail")
                        {
                            System.IO.File.Move(file, System.IO.Path.Combine(calcFailDir, System.IO.Path.GetFileName(file)), true);
                            calc_fail_counter++;
                        }
                        else if (classification.Category == "SimFail")
                        {
                            System.IO.File.Move(file, System.IO.Path.Combine(simFailDir, System.IO.Path.GetFileName(file)), true);
                            sim_fail_counter++;
                        }
                        else
                        {
                            System.IO.File.Move(file, System.IO.Path.Combine(goodsDir, System.IO.Path.GetFileName(file)), true);
                            good_counter++;
                        }

                        checked_couner++;
                        filesProcessed++;
                    }
                }
                catch (Exception ex)
                {
                    // log the exception
                    writeLog("Error scanning file " + file + ": " + ex.Message);
                }
            }
            
            return filesProcessed;
        }

        private void updateUI()
        {
            try
            {
                checkedBlock.Text = checked_couner.ToString();
                failcalcBlock.Text = calc_fail_counter.ToString();
                failsimBlock.Text = sim_fail_counter.ToString();
                goodBlock.Text = good_counter.ToString();

                var passrate = (good_counter / (checked_couner + 0.0001) * 100);
                if (checked_couner == 0)
                {
                    passrate = 100;
                }

                passRateBlock.Text = passrate.ToString("0") + "%";

                var elapsed = uptimeStopWatch.Elapsed;
                uptimeBlock.Text = string.Format("{0:00}:{1:00}:{2:00}",
                    (int)elapsed.TotalHours,
                    elapsed.Minutes,
                    elapsed.Seconds);



                if (passrate > 99)
                {
                    passRateBlock.Foreground = Brushes.Black;
                }
                else if (passrate > 80)
                {
                    passRateBlock.Foreground = failcalcBlock.Foreground;
                }
                else
                {
                    passRateBlock.Foreground = failcalcBlock.Foreground;
                }

                if (cpuUsage > 80)
                {
                    cpuBlock.Foreground = failcalcBlock.Foreground;
                }
                else if (cpuUsage > 60)
                {
                    cpuBlock.Foreground = failsimBlock.Foreground;
                }
                else
                {
                    cpuBlock.Foreground = Brushes.Black;
                }
                if (ramUsage > 80)
                {
                    ramBlock.Foreground = failcalcBlock.Foreground;
                }
                else if (ramUsage > 60)
                {
                    ramBlock.Foreground = failsimBlock.Foreground;
                }
                else
                {
                    ramBlock.Foreground = Brushes.Black;
                }

                // Animate progress bars smoothly
                if (SYSTEM_USAGE_MONITORING_CPU && cpuBlock != null)
                {
                    AnimateProgressBar(cpuBlock, cpuUsage);
                }
                if (SYSTEM_USAGE_MONITORING_RAM && ramBlock != null)
                {
                    AnimateProgressBar(ramBlock, ramUsage);
                }
            } catch (Exception ex)
            {
                writeLog("Error: " + ex.Message);
            }
            
        }
        
        /// <summary>
        /// Shows an Apple-style confirmation dialog
        /// </summary>
        private bool ShowAppleConfirmDialog(string title, string message, string confirmText = "Yes", string cancelText = "No")
        {
            bool result = false;
            
            var dialog = new Window
            {
                Title = title,
                Width = 420,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            
            dialog.KeyDown += (s, args) => { if (args.Key == System.Windows.Input.Key.Escape) dialog.Close(); };

            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(231, 227, 242)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24)
            };
            outerBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.15
            };

            var mainPanel = new StackPanel();
            
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(32, 27, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            mainPanel.Children.Add(titleBlock);

            var messageBlock = new TextBlock
            {
                Text = message,
                FontSize = 14,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(131, 125, 150)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 24)
            };
            mainPanel.Children.Add(messageBlock);

            var buttonsPanel = new Grid();
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Cancel button
            var cancelBtn = CreateAppleDialogButton(cancelText, false);
            cancelBtn.Click += (s, args) => { result = false; dialog.Close(); };
            Grid.SetColumn(cancelBtn, 0);
            buttonsPanel.Children.Add(cancelBtn);

            // Confirm button (primary)
            var confirmBtn = CreateAppleDialogButton(confirmText, true);
            confirmBtn.Click += (s, args) => { result = true; dialog.Close(); };
            Grid.SetColumn(confirmBtn, 2);
            buttonsPanel.Children.Add(confirmBtn);

            mainPanel.Children.Add(buttonsPanel);

            outerBorder.Child = mainPanel;
            dialog.Content = outerBorder;
            dialog.ShowDialog();
            
            return result;
        }

        /// <summary>
        /// Creates an Apple-style button for dialogs
        /// </summary>
        private Button CreateAppleDialogButton(string content, bool isPrimary)
        {
            var btn = new Button
            {
                Content = content,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var normalBrush = isPrimary ? new SolidColorBrush(Color.FromRgb(122, 90, 248)) :
                              new SolidColorBrush(Color.FromRgb(246, 244, 251));
            var hoverBrush = isPrimary ? new SolidColorBrush(Color.FromRgb(107, 72, 232)) :
                             new SolidColorBrush(Color.FromRgb(231, 227, 242));
            var foreground = isPrimary ? System.Windows.Media.Brushes.White : 
                             new SolidColorBrush(Color.FromRgb(32, 27, 51));

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "border";
            borderFactory.SetValue(Border.BackgroundProperty, normalBrush);
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(16, 12, 16, 12));
            
            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentPresenter);
            
            template.VisualTree = borderFactory;

            var mouseOverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "border"));
            template.Triggers.Add(mouseOverTrigger);

            btn.Template = template;
            btn.Foreground = foreground;

            return btn;
        }

        /// <summary>
        /// Auto-detect ENCY installation path
        /// </summary>
        private bool AutoDetectEncyPath()
        {
            // First try NB (nightly build)
            string nbPath = @"C:\Program Files\ENCY Software\ENCY NB 3\Bin64\ency.exe";
            if (File.Exists(nbPath))
            {
                CAM_EXE_PATH = nbPath;
                writeLog($"Auto-detected ENCY NB: {CAM_EXE_PATH}");
                SaveEncyPath();
                return true;
            }

            // Then try Release
            string rlPath = @"C:\Program Files\ENCY Software\ENCY 3\Bin64\ency.exe";
            if (File.Exists(rlPath))
            {
                CAM_EXE_PATH = rlPath;
                writeLog($"Auto-detected ENCY Release: {CAM_EXE_PATH}");
                SaveEncyPath();
                return true;
            }

            writeLog("ENCY not found in standard locations");
            return false;
        }

        /// <summary>
        /// Update the ENCY path display in UI
        /// </summary>
        private void UpdateEncyPathDisplay()
        {
            if (EncyPathText != null)
            {
                if (!string.IsNullOrEmpty(CAM_EXE_PATH) && File.Exists(CAM_EXE_PATH))
                {
                    EncyPathText.Text = CAM_EXE_PATH;
                    EncyPathText.Foreground = new SolidColorBrush(Color.FromRgb(32, 27, 51));
                }
                else
                {
                    EncyPathText.Text = "ENCY not found - click ... to select";
                    EncyPathText.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));
                }
            }
        }

        /// <summary>
        /// Save ENCY path to settings
        /// </summary>
        private void SaveEncyPath()
        {
            var settings = new AppSettings
            {
                SelectedCAMPath = CAM_EXE_PATH,
                SystemUsageMonitoringCpu = SYSTEM_USAGE_MONITORING_CPU,
                SystemUsageMonitoringRam = SYSTEM_USAGE_MONITORING_RAM
            };
            saveSettings(SETTINGS_FN, settings);
        }

        /// <summary>
        /// Handle change ENCY path button click
        /// </summary>
        private void ChangeEncyPath_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "ENCY or SprutCAM|ency.exe;sc.exe";
            openFileDialog.Title = "Select ENCY executable";
            
            if (!string.IsNullOrEmpty(CAM_EXE_PATH) && File.Exists(CAM_EXE_PATH))
            {
                openFileDialog.InitialDirectory = System.IO.Path.GetDirectoryName(CAM_EXE_PATH);
            }
            
            if (openFileDialog.ShowDialog() == true)
            {
                var fn = openFileDialog.FileName;
                if (File.Exists(fn))
                {
                    CAM_EXE_PATH = fn;
                    writeLog($"User selected CAM path: {CAM_EXE_PATH}");
                    SaveEncyPath();
                    UpdateEncyPathDisplay();
                }
            }
        }

        /// <summary>
        /// Shows an Apple-style info dialog
        /// </summary>
        private void ShowAppleInfoDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 380,
                Height = 160,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            
            // Only set owner if main window is visible
            if (this.IsVisible)
            {
                dialog.Owner = this;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            
            dialog.KeyDown += (s, args) => { if (args.Key == System.Windows.Input.Key.Escape) dialog.Close(); };

            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(231, 227, 242)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24)
            };
            outerBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.15
            };

            var mainPanel = new StackPanel();
            
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(32, 27, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            mainPanel.Children.Add(titleBlock);

            var messageBlock = new TextBlock
            {
                Text = message,
                FontSize = 14,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(131, 125, 150)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };
            mainPanel.Children.Add(messageBlock);

            var okBtn = CreateAppleDialogButton("OK", true);
            okBtn.HorizontalAlignment = HorizontalAlignment.Center;
            okBtn.Click += (s, args) => dialog.Close();
            mainPanel.Children.Add(okBtn);

            outerBorder.Child = mainPanel;
            dialog.Content = outerBorder;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Animates a progress bar value smoothly without memory leaks
        /// </summary>
        private void AnimateProgressBar(ProgressBar progressBar, double toValue)
        {
            if (progressBar == null) return;
            
            try
            {
                // Clamp value to valid range
                toValue = Math.Max(0, Math.Min(100, toValue));
                
                // Create animation with FillBehavior.Stop to prevent memory leaks
                var animation = new DoubleAnimation
                {
                    To = toValue,
                    Duration = TimeSpan.FromMilliseconds(400),
                    FillBehavior = FillBehavior.Stop,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                
                // Set the final value when animation completes
                animation.Completed += (s, e) => 
                {
                    try
                    {
                        progressBar.Value = toValue;
                    }
                    catch { }
                };
                
                progressBar.BeginAnimation(ProgressBar.ValueProperty, animation);
            }
            catch
            {
                // Fallback to direct assignment if animation fails
                try
                {
                    progressBar.Value = toValue;
                }
                catch { }
            }
        }

        private async Task wait(int seconds)
        {
            await Task.Delay(seconds * 1000);
        }

        public  MainWindow()
        {
            InitializeComponent();
            
            // Subscribe to card click events
            ErrorsCard.MouseLeftButtonDown += (s, e) => OpenResultsWithFilter("CalcFail");
            WarningsCard.MouseLeftButtonDown += (s, e) => OpenResultsWithFilter("SimFail");
            PassedCard.MouseLeftButtonDown += (s, e) => OpenResultsWithFilter("Good");
            TotalCard.MouseLeftButtonDown += (s, e) => OpenResultsWithFilter("All");
        }

        private void UiUpdateTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                // Verify UI elements are available first
                if (checkedBlock == null || failcalcBlock == null || failsimBlock == null || goodBlock == null || uptimeBlock == null)
                {
                    return; // UI not ready yet
                }

                // Capture current counter values (thread-safe)
                int currentChecked, currentGood, currentCalcFail, currentSimFail, currentCpuUsage, currentRamUsage;
                lock (_countersLock)
                {
                    currentChecked = checked_couner;
                    currentGood = good_counter;
                    currentCalcFail = calc_fail_counter;
                    currentSimFail = sim_fail_counter;
                }
                
                // Capture CPU and RAM usage (these are updated in background thread)
                currentCpuUsage = cpuUsage;
                currentRamUsage = ramUsage;

                // Update counters
                checkedBlock.Text = currentChecked.ToString();
                failcalcBlock.Text = currentCalcFail.ToString();
                failsimBlock.Text = currentSimFail.ToString();
                goodBlock.Text = currentGood.ToString();

                // Calculate and update pass rate
                var passrate = (currentGood / (currentChecked + 0.0001) * 100);
                if (currentChecked == 0)
                {
                    passrate = 100;
                }
                passRateBlock.Text = passrate.ToString("0") + "%";

                // Update uptime - always update
                var elapsed = uptimeStopWatch.Elapsed;
                uptimeBlock.Text = string.Format("{0:00}:{1:00}:{2:00}",
                    (int)elapsed.TotalHours,
                    elapsed.Minutes,
                    elapsed.Seconds);

                // Update pass rate color
                if (passrate > 99)
                {
                    passRateBlock.Foreground = Brushes.Black;
                }
                else if (passrate > 80)
                {
                    passRateBlock.Foreground = failcalcBlock.Foreground;
                }
                else
                {
                    passRateBlock.Foreground = failcalcBlock.Foreground;
                }

                // Update CPU color
                if (currentCpuUsage > 80)
                {
                    cpuBlock.Foreground = failcalcBlock.Foreground;
                }
                else if (currentCpuUsage > 60)
                {
                    cpuBlock.Foreground = failsimBlock.Foreground;
                }
                else
                {
                    cpuBlock.Foreground = Brushes.Black;
                }

                // Update RAM color
                if (currentRamUsage > 80)
                {
                    ramBlock.Foreground = failcalcBlock.Foreground;
                }
                else if (currentRamUsage > 60)
                {
                    ramBlock.Foreground = failsimBlock.Foreground;
                }
                else
                {
                    ramBlock.Foreground = Brushes.Black;
                }

                // Update CPU and RAM progress bars (only if monitoring is enabled)
                // Use smooth animation for value changes
                if (SYSTEM_USAGE_MONITORING_CPU && cpuBlock != null)
                {
                    try
                    {
                        // Ensure progress bar is enabled and visible
                        if (!cpuBlock.IsEnabled)
                        {
                            cpuBlock.IsEnabled = true;
                        }
                        if (cpuBlock.Visibility != Visibility.Visible)
                        {
                            cpuBlock.Visibility = Visibility.Visible;
                        }
                        
                        // Ensure progress bar has correct range
                        if (cpuBlock.Maximum != 100)
                        {
                            cpuBlock.Maximum = 100;
                        }
                        if (cpuBlock.Minimum != 0)
                        {
                            cpuBlock.Minimum = 0;
                        }
                        
                        // Animate value smoothly
                        AnimateProgressBar(cpuBlock, currentCpuUsage);
                    }
                    catch (Exception animEx)
                    {
                        writeLog("Error updating CPU progress bar: " + animEx.Message);
                    }
                }
                else if (!SYSTEM_USAGE_MONITORING_CPU && cpuBlock != null)
                {
                    // Reset to 0 when monitoring is disabled
                    try
                    {
                        AnimateProgressBar(cpuBlock, 0);
                    }
                    catch { }
                }

                if (SYSTEM_USAGE_MONITORING_RAM && ramBlock != null)
                {
                    try
                    {
                        // Ensure progress bar is enabled and visible
                        if (!ramBlock.IsEnabled)
                        {
                            ramBlock.IsEnabled = true;
                        }
                        if (ramBlock.Visibility != Visibility.Visible)
                        {
                            ramBlock.Visibility = Visibility.Visible;
                        }
                        
                        // Ensure progress bar has correct range
                        if (ramBlock.Maximum != 100)
                        {
                            ramBlock.Maximum = 100;
                        }
                        if (ramBlock.Minimum != 0)
                        {
                            ramBlock.Minimum = 0;
                        }
                        
                        // Animate value smoothly
                        AnimateProgressBar(ramBlock, currentRamUsage);
                    }
                    catch (Exception animEx)
                    {
                        writeLog("Error updating RAM progress bar: " + animEx.Message);
                    }
                }
                else if (!SYSTEM_USAGE_MONITORING_RAM && ramBlock != null)
                {
                    // Reset to 0 when monitoring is disabled
                    try
                    {
                        AnimateProgressBar(ramBlock, 0);
                    }
                    catch { }
                }

                // check is ProjectTester is still running
                var testerRunning = Process.GetProcessesByName("ProjectTestingUtility").Length > 0;
                if (uptimeStopWatch.IsRunning && !testerRunning)
                {
                    writeLog("ProjectTester stopped");
                    // Path selector enabled

                    StopBtn.Visibility = Visibility.Collapsed;
                    RunBtn.Visibility = Visibility.Visible;

                    uptimeStopWatch.Stop();
                }
            }
            catch (Exception ex)
            {
                writeLog("Error in UI update timer: " + ex.Message);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                // Stop UI update timer
                if (uiUpdateTimer != null)
                {
                    uiUpdateTimer.Stop();
                    uiUpdateTimer = null;
                }

                // Dispose PerformanceCounters to prevent memory leaks
                try
                {
                    ramAvailable?.Dispose();
                }
                catch { }
                ramAvailable = null;

                try
                {
                    cpuCounter?.Dispose();
                }
                catch { }
                cpuCounter = null;

                // Cancel monitoring loop
                _cts?.Cancel();
            }
            catch { }
            
            base.OnClosed(e);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //backup log file
            var logFn = Path.Combine(WORK_DIR, "log.txt");
            if (File.Exists(logFn))
            {
                var logBackupFn = Path.Combine(WORK_DIR, "log_backup.txt");
                File.Copy(logFn, logBackupFn, true);
                try
                {
                    File.Delete(logFn);
                } catch (Exception ex)
                {
                    writeLog("Error: " + ex.Message);
                }
            }

            writeLog("Application started");

            // load settings if exists
            var settings = loadSettings(SETTINGS_FN);
            bool encyFound = false;
            
            string savedPath = settings?.SelectedCAMPath as string;
            if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath))
            {
                // Use saved path if it exists
                CAM_EXE_PATH = savedPath;
                writeLog($"Using saved CAM path: {CAM_EXE_PATH}");
                encyFound = true;
            }
            else
            {
                // Auto-detect ENCY path
                encyFound = AutoDetectEncyPath();
            }
            
            // Update UI with path
            try
            {
                UpdateEncyPathDisplay();
            }
            catch (Exception ex)
            {
                writeLog("Error updating ENCY path display: " + ex.Message);
            }
            
            if (!encyFound)
            {
                // Show dialog to select path after window is fully loaded
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ShowAppleInfoDialog("ENCY Not Found", "Could not find ENCY installation. Please select the ency.exe location.");
                        ChangeEncyPath_Click(null, null);
                        UpdateEncyPathDisplay();
                    }
                    catch (Exception ex)
                    {
                        writeLog("Error showing ENCY dialog: " + ex.Message);
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            
            if (settings != null)
            {

                try
                {
                    SYSTEM_USAGE_MONITORING_CPU = settings.SystemUsageMonitoringCpu ?? false;
                    if (cpuBlock != null)
                    {
                        cpuBlock.IsEnabled = SYSTEM_USAGE_MONITORING_CPU;
                    }
                    if (chkbxCPU != null)
                    {
                        chkbxCPU.IsChecked = SYSTEM_USAGE_MONITORING_CPU;
                    }

                    SYSTEM_USAGE_MONITORING_RAM = settings.SystemUsageMonitoringRam ?? false;
                    if (ramBlock != null)
                    {
                        ramBlock.IsEnabled = SYSTEM_USAGE_MONITORING_RAM;
                    }
                    if (chkbxMem != null)
                    {
                        chkbxMem.IsChecked = SYSTEM_USAGE_MONITORING_RAM;
                    }
                }
                catch (Exception ex)
                {
                    writeLog("Error loading monitoring settings: " + ex.Message);
                }
            }


            // Create the folders
            var curDir = System.IO.Path.Combine(WORK_DIR, "results");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(curDir, GOODS_FOLDER));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(curDir, CALC_FAIL_FOLDER));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(curDir, SIM_FAIL_FOLDER));
            writeLog("Results folders created");

            // count json files in goods folder. check the names
            var goodsFiles = System.IO.Directory.GetFiles(System.IO.Path.Combine(curDir, GOODS_FOLDER), "*.json");
            writeLog("Scanning results folders");
            void CountExistingResult(string file)
            {
                var classification = ResultClassification.Read(Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file)));
                if (classification.Category == "Good") good_counter++;
                else if (classification.Category == "SimFail") sim_fail_counter++;
                else calc_fail_counter++;
            }
            foreach (var file in goodsFiles)
            {
                try
                {
                    var id = int.Parse(System.IO.Path.GetFileNameWithoutExtension(file));
                    if (id < lastId)
                    {
                        lastId = id;
                    }
                    CountExistingResult(file);
                } catch (Exception ex)
                {
                    writeLog("Error: " + ex.Message);
                }
            }

            // same for calc_fail
            var calcFailFiles = System.IO.Directory.GetFiles(System.IO.Path.Combine(curDir, CALC_FAIL_FOLDER), "*.json");
            foreach (var file in calcFailFiles)
            {
                try
                {
                    var id = int.Parse(System.IO.Path.GetFileNameWithoutExtension(file));
                    if (id < lastId)
                    {
                        lastId = id;
                    }
                    CountExistingResult(file);
                } catch (Exception ex)
                {
                    writeLog("Error: " + ex.Message);
                }
            }

            // same for sim_fail
            var simFailFiles = System.IO.Directory.GetFiles(System.IO.Path.Combine(curDir, SIM_FAIL_FOLDER), "*.json");
            foreach (var file in simFailFiles)
            {
                try
                {
                    var id = int.Parse(System.IO.Path.GetFileNameWithoutExtension(file));
                    if (id < lastId)
                    {
                        lastId = id;
                    }
                    CountExistingResult(file);
                } catch (Exception ex)
                {
                    writeLog("Error: " + ex.Message);
                }
            }

            checked_couner = good_counter + calc_fail_counter + sim_fail_counter;
            writeLog("Results folders scanned");

            if (checked_couner > 0)
            {
                writeLog("Previous test session found");
                // ask do you want to continue or clear results
                var confirmed = ShowAppleConfirmDialog(
                    "Previous Session Found",
                    "Delete results and start a new session?",
                    "Delete & Start New",
                    "Cancel");
                if (confirmed)
                {

                    // delete projects dir

                    var projectsDir = System.IO.Path.Combine(WORK_DIR, "projects");
                    if (System.IO.Directory.Exists(projectsDir))
                    {
                        writeLog("Old projects folder found");
                        try
                        {
                            System.IO.Directory.Delete(projectsDir, true);
                        }
                        catch (Exception ex)
                        {
                            writeLog("Error: " + ex.Message);
                        }
                    }

                    writeLog("Old results removed");
                    System.IO.Directory.Delete(System.IO.Path.Combine(curDir, GOODS_FOLDER), true);
                    System.IO.Directory.Delete(System.IO.Path.Combine(curDir, CALC_FAIL_FOLDER), true);
                    System.IO.Directory.Delete(System.IO.Path.Combine(curDir, SIM_FAIL_FOLDER), true);

                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(curDir, GOODS_FOLDER));
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(curDir, CALC_FAIL_FOLDER));
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(curDir, SIM_FAIL_FOLDER));

                    good_counter = 0;
                    calc_fail_counter = 0;
                    sim_fail_counter = 0;
                    checked_couner = 0;
                    lastId = int.MaxValue;
                }
            }
            
            extractResources();

            // Store directory for monitoring
            monitoringCurDir = curDir;

            // Start background monitoring loop
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        // Scan for new results (this updates counters) - always execute
                        int filesScanned = scan(monitoringCurDir, 
                            System.IO.Path.Combine(monitoringCurDir, GOODS_FOLDER), 
                            System.IO.Path.Combine(monitoringCurDir, CALC_FAIL_FOLDER), 
                            System.IO.Path.Combine(monitoringCurDir, SIM_FAIL_FOLDER));
                        
                        if (filesScanned > 0)
                        {
                            writeLog($"Scanned {filesScanned} new result file(s)");
                        }
                    }
                    catch (Exception scanEx)
                    {
                        writeLog("Error in scan: " + scanEx.Message);
                    }

                    // Calculate CPU and RAM usage - execute independently (non-blocking)
                    try
                    {
                        if (SYSTEM_USAGE_MONITORING_CPU || SYSTEM_USAGE_MONITORING_RAM)
                        {
                            calcCpuAndRamUsage();
                        }
                    }
                    catch (Exception cpuRamEx)
                    {
                        writeLog("Error in calcCpuAndRamUsage: " + cpuRamEx.Message);
                    }

                    // await 1 second before next iteration
                    await Task.Delay(1000);
                }
            });

            // Start UI update timer (runs on UI thread)
            uiUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            uiUpdateTimer.Start();

        }

        Stopwatch uptimeStopWatch = new Stopwatch();

        private async void RunBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            lastCheckedProject = int.MaxValue;
            sameProjectFailCounter = 0;
            uptimeStopWatch.Reset();
            if (StartUtility(retryLastProject: true))
                await MonitorLoop("ProjectTestingUtility");
        }

        private async void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = ShowAppleConfirmDialog(
                "Stop Testing",
                "Are you sure you want to stop the current test session?",
                "Stop",
                "Cancel");

            if (confirmed)
            {
                _cts.Cancel();
                await Task.Delay(2000);
                // kill the tester end camexe
                Process.GetProcessesByName("ProjectTestingUtility").ToList().ForEach(p => p.Kill());
                // kill CAM_EXE
                Process.GetProcessesByName("ency").ToList().ForEach(p => p.Kill());
                Process.GetProcessesByName("sc").ToList().ForEach(p => p.Kill());
                Process.GetProcessesByName("sc_prime").ToList().ForEach(p => p.Kill());
                Process.GetProcessesByName("ency_prime").ToList().ForEach(p => p.Kill());

                RestoreStoppedControls();
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Process.GetProcessesByName("ProjectTestingUtility").ToList().ForEach(p => p.Kill());
            // kill CAM_EXE
            Process.GetProcessesByName("ency").ToList().ForEach(p => p.Kill());
            Process.GetProcessesByName("sc").ToList().ForEach(p => p.Kill());
            Process.GetProcessesByName("sc_prime").ToList().ForEach(p => p.Kill());
            Process.GetProcessesByName("ency_prime").ToList().ForEach(p => p.Kill());
            Close();
        }


        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new SettingsWindow();
                settingsWindow.Owner = this;
                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                writeLog("Error opening settings window: " + ex.Message);
                MessageBox.Show("Failed to open settings window. See log file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResultsBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isTestingRunning = StopBtn.Visibility == Visibility.Visible;
                var resultsWindow = new ResultsWindow("All", CAM_EXE_PATH, isTestingRunning);
                resultsWindow.Owner = this;
                resultsWindow.Show();
            }
            catch (Exception ex)
            {
                writeLog("Error opening results window: " + ex.Message);
                MessageBox.Show("Failed to open results window. See log file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenResultsWithFilter(string filter)
        {
            try
            {
                bool isTestingRunning = StopBtn.Visibility == Visibility.Visible;
                var resultsWindow = new ResultsWindow(filter, CAM_EXE_PATH, isTestingRunning);
                resultsWindow.Owner = this;
                resultsWindow.Show();
            }
            catch (Exception ex)
            {
                writeLog($"Error opening results window with filter {filter}: " + ex.Message);
            }
        }


        private void chkbxCPU_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SYSTEM_USAGE_MONITORING_CPU = chkbxCPU.IsChecked == true;
                
                // Save settings synchronously to ensure they are saved
                try
                {
                    var settings = new AppSettings
                    {
                        SelectedCAMPath = CAM_EXE_PATH,
                        SystemUsageMonitoringCpu = SYSTEM_USAGE_MONITORING_CPU,
                        SystemUsageMonitoringRam = SYSTEM_USAGE_MONITORING_RAM
                    };
                    if (saveSettings(SETTINGS_FN, settings))
                    {
                        writeLog("CPU monitoring settings saved successfully");
                    }
                    else
                    {
                        writeLog("Failed to save CPU monitoring settings");
                    }
                }
                catch (Exception ex)
                {
                    writeLog("Error saving settings: " + ex.Message);
                }

                if (SYSTEM_USAGE_MONITORING_CPU)
                {
                    writeLog("CPU usage monitoring enabled");
                    // Reset counter to force re-initialization
                    try
                    {
                        cpuCounter?.Dispose();
                    }
                    catch { }
                    cpuCounter = null;
                    cpuCounterInitializing = false;
                    cpuUsage = 0; // Reset value
                    
                    // Ensure progress bar is visible and enabled
                    if (cpuBlock != null)
                    {
                        cpuBlock.IsEnabled = true;
                        cpuBlock.Visibility = Visibility.Visible;
                        cpuBlock.Maximum = 100;
                        cpuBlock.Minimum = 0;
                        AnimateProgressBar(cpuBlock, 0);
                    }
                    
                    // Initialize CPU counter immediately in background
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                            counter.NextValue();
                            System.Threading.Thread.Sleep(100);
                            cpuCounter = counter;
                            writeLog("CPU counter initialized from checkbox click");
                        }
                        catch (Exception ex)
                        {
                            writeLog("Error initializing CPU counter from checkbox: " + ex.Message);
                        }
                    });
                }
                else
                {
                    writeLog("CPU usage monitoring disabled");
                    // Dispose counter when disabled
                    try
                    {
                        cpuCounter?.Dispose();
                    }
                    catch { }
                    cpuCounter = null;
                }
                cpuBlock.IsEnabled = SYSTEM_USAGE_MONITORING_CPU;
            }
            catch (Exception ex)
            {
                writeLog("Error in chkbxCPU_Click: " + ex.Message);
            }
        }

        private void chkbxMem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SYSTEM_USAGE_MONITORING_RAM = chkbxMem.IsChecked == true;
                
                // Save settings synchronously to ensure they are saved
                try
                {
                    var settings = new AppSettings
                    {
                        SelectedCAMPath = CAM_EXE_PATH,
                        SystemUsageMonitoringCpu = SYSTEM_USAGE_MONITORING_CPU,
                        SystemUsageMonitoringRam = SYSTEM_USAGE_MONITORING_RAM
                    };
                    if (saveSettings(SETTINGS_FN, settings))
                    {
                        writeLog("RAM monitoring settings saved successfully");
                    }
                    else
                    {
                        writeLog("Failed to save RAM monitoring settings");
                    }
                }
                catch (Exception ex)
                {
                    writeLog("Error saving settings: " + ex.Message);
                }

                if (SYSTEM_USAGE_MONITORING_RAM)
                {
                    writeLog("RAM usage monitoring enabled");
                    // Reset counter to force re-initialization
                    try
                    {
                        ramAvailable?.Dispose();
                    }
                    catch { }
                    ramAvailable = null;
                    ramCounterInitializing = false;
                    ramUsage = 0; // Reset value
                    
                    // Ensure progress bar is visible and enabled
                    if (ramBlock != null)
                    {
                        ramBlock.IsEnabled = true;
                        ramBlock.Visibility = Visibility.Visible;
                        ramBlock.Maximum = 100;
                        ramBlock.Minimum = 0;
                        AnimateProgressBar(ramBlock, 0);
                    }
                    
                    // Initialize RAM counter immediately in background
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var counter = new PerformanceCounter("Memory", "Available MBytes");
                            counter.NextValue();
                            System.Threading.Thread.Sleep(100);
                            ramAvailable = counter;
                            
                            // Also initialize total RAM
                            if (!ramTotalInitialized)
                            {
                                ramTotalInitialized = true;
                                getTotalMemoryInMB();
                            }
                            
                            writeLog("RAM counter initialized from checkbox click");
                        }
                        catch (Exception ex)
                        {
                            writeLog("Error initializing RAM counter from checkbox: " + ex.Message);
                        }
                    });
                }
                else
                {
                    writeLog("RAM usage monitoring disabled");
                    // Dispose counter when disabled
                    try
                    {
                        ramAvailable?.Dispose();
                    }
                    catch { }
                    ramAvailable = null;
                }
                ramBlock.IsEnabled = SYSTEM_USAGE_MONITORING_RAM;
            }
            catch (Exception ex)
            {
                writeLog("Error in chkbxMem_Click: " + ex.Message);
            }
        }
    }
}
