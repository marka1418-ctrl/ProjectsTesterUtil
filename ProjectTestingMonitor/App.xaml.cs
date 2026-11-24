using System.Configuration;
using System.Data;
using System.IO.Pipes;
using System.IO;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectTesterMonitor
{
    public partial class App : Application
    {
        private const string UniqueMutexName = "ProjectTester_Mutex_12345678";
        private const string PipeName = "ProjectTester_Pipe_12345678";
        private Mutex? _mutex;
        private bool _ownsMutex = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Global exception handlers for diagnostics
            this.DispatcherUnhandledException += (s, ev) =>
            {
                LogException(ev.Exception, "DispatcherUnhandledException");
                ev.Handled = true;
                MessageBox.Show("Unhandled UI exception occurred. See log file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                LogException(ev.ExceptionObject as Exception, "AppDomainUnhandledException");
            };

            bool createdNew = false;
            try
            {
                _mutex = new Mutex(true, UniqueMutexName, out createdNew);
                _ownsMutex = createdNew;
            }
            catch (Exception ex)
            {
                LogException(ex, "MutexCreation");
                // Continue anyway - might be permission issue
                _mutex = null;
                _ownsMutex = false;
                createdNew = false;
            }

            if (!createdNew)
            {
                // Send command to existing instance to show the window
                try
                {
                    using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        client.Connect(1000); // wait 1s
                        using (var writer = new StreamWriter(client))
                        {
                            writer.WriteLine("SHOW");
                            writer.Flush();
                        }
                    }
                }
                catch
                {
                    // existing instance not responding (ignore)
                }

                // Clean up mutex before shutdown
                if (_mutex != null)
                {
                    try
                    {
                        _mutex.Dispose();
                    }
                    catch { }
                    _mutex = null;
                }
                _ownsMutex = false;
                Shutdown();
                return;
            }

            // Start the NamedPipe server for other instances to contact
            Task.Run(() => StartPipeServer());

            // Create and show the main window (and set Application.MainWindow explicitly)
            try
            {
                var mainWindow = new MainWindow();
                Application.Current.MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                LogException(ex, "ShowMainWindow");
                MessageBox.Show("Failed to show main window. See log file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        private void StartPipeServer()
        {
            while (true)
            {
                using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In))
                {
                    server.WaitForConnection();
                    using (var reader = new StreamReader(server))
                    {
                        var message = reader.ReadLine();
                        if (message == "SHOW")
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (Application.Current.MainWindow is { } mainWindow)
                                {
                                    if (mainWindow.WindowState == WindowState.Minimized)
                                        mainWindow.WindowState = WindowState.Normal;

                                    mainWindow.Activate();
                                    mainWindow.Topmost = true;    // bring to front
                                    mainWindow.Topmost = false;   // then unset Topmost
                                    mainWindow.Focus();
                                }
                            });
                        }
                    }
                }
            }
        }

        private void LogException(Exception ex, string context)
        {
            try
            {
                var logPath = AppPaths.Combine("app_error.log");
                var text = $"[{DateTime.Now:O}] {context}: {ex?.ToString() ?? "null"}{Environment.NewLine}";
                File.AppendAllText(logPath, text);
            }
            catch { /* best-effort logging */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Only release mutex if we own it
                if (_mutex != null && _ownsMutex)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore - mutex might already be released
                    }
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
            finally
            {
                try
                {
                    _mutex?.Dispose();
                }
                catch { }
                _mutex = null;
                _ownsMutex = false;
            }
            base.OnExit(e);
        }
    }
}
