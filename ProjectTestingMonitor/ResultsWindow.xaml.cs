using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Newtonsoft.Json.Linq;

namespace ProjectTesterMonitor
{
    public partial class ResultsWindow : Window
    {
        private const uint WdaExcludeFromCapture = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        private string resultsDir;
        private string projectsDir;
        private string goodsFolder;
        private string calcFailFolder;
        private string simFailFolder;
        private string currentFilter = "All";
        private string camExePath;
        private bool isTestingRunning;
        
        // Auto-refresh
        private DispatcherTimer autoRefreshTimer;
        private Dictionary<string, string> cachedFileVersions = new Dictionary<string, string>();
        private List<ResultItem> allResults = new List<ResultItem>();

        private class ResultItem
        {
            public string ProjectId { get; set; }
            public string Status { get; set; }
            public string ProjectFilePath { get; set; }  // Path to .stcp file
            public string FilePath { get; set; }  // Path to .json file
            public Brush StatusColor { get; set; }
            public string Category { get; set; }
        }

        public ResultsWindow() : this("All", null, false)
        {
        }

        public ResultsWindow(string initialFilter) : this(initialFilter, null, false)
        {
        }

        public ResultsWindow(string initialFilter, string encyPath, bool testingRunning)
        {
            InitializeComponent();
            // Keep the statistics window visible to the operator while excluding
            // it from desktop/Windows capture used for the test recording.
            SourceInitialized += (_, _) => ExcludeFromScreenCapture();
            
            camExePath = encyPath;
            isTestingRunning = testingRunning;
            
            resultsDir = AppPaths.Combine("results");
            projectsDir = AppPaths.Combine("projects");
            goodsFolder = Path.Combine(resultsDir, "goods");
            calcFailFolder = Path.Combine(resultsDir, "calc_fail");
            simFailFolder = Path.Combine(resultsDir, "sim_fail");

            // Set initial filter
            currentFilter = initialFilter;
            
            LoadResults();
            
            // Apply initial filter button highlight
            ApplyFilterHighlight(initialFilter);
            
            // Start auto-refresh timer (every 2 seconds)
            autoRefreshTimer = new DispatcherTimer();
            autoRefreshTimer.Interval = TimeSpan.FromSeconds(2);
            autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            autoRefreshTimer.Start();
            
            // Stop timer when window closes
            Closed += (s, e) => 
            {
                autoRefreshTimer?.Stop();
                autoRefreshTimer = null;
            };
        }

        private void ExcludeFromScreenCapture()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero && !SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
                    System.Diagnostics.Debug.WriteLine($"Could not exclude ResultsWindow from capture (Win32 {Marshal.GetLastWin32Error()}).");
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not exclude ResultsWindow from capture: {exception.Message}");
            }
        }

        private void ApplyFilterHighlight(string filter)
        {
            // Reset all button backgrounds
            AllBtn.Background = Brushes.Transparent;
            GoodBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19027F0F"));
            CalcFailBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19FC0606"));
            SimFailBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19D6B102"));

            // Highlight the selected button
            var highlightBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD0D0D0"));
            switch (filter)
            {
                case "All":
                    AllBtn.Background = highlightBrush;
                    break;
                case "Good":
                    GoodBtn.Background = highlightBrush;
                    break;
                case "CalcFail":
                    CalcFailBtn.Background = highlightBrush;
                    break;
                case "SimFail":
                    SimFailBtn.Background = highlightBrush;
                    break;
            }
        }
        
        private void AutoRefreshTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Check if there are new files without blocking UI
                var currentFiles = new Dictionary<string, string>();
                
                if (Directory.Exists(goodsFolder))
                {
                    foreach (var file in Directory.GetFiles(goodsFolder, "*.json"))
                        currentFiles[file] = GetFileVersion(file);
                }
                if (Directory.Exists(calcFailFolder))
                {
                    foreach (var file in Directory.GetFiles(calcFailFolder, "*.json"))
                        currentFiles[file] = GetFileVersion(file);
                }
                if (Directory.Exists(simFailFolder))
                {
                    foreach (var file in Directory.GetFiles(simFailFolder, "*.json"))
                        currentFiles[file] = GetFileVersion(file);
                }
                
                // Only refresh if files changed
                if (currentFiles.Count != cachedFileVersions.Count ||
                    currentFiles.Any(file => !cachedFileVersions.TryGetValue(file.Key, out var version) || version != file.Value))
                {
                    // Save selected item
                    string selectedFilePath = null;
                    if (ResultsListBox.SelectedItem is ResultItem selected)
                    {
                        selectedFilePath = selected.FilePath;
                    }
                    
                    // Reload results
                    LoadResultsInternal();
                    
                    // Restore selection
                    if (selectedFilePath != null)
                    {
                        var itemToSelect = allResults.FirstOrDefault(r => r.FilePath == selectedFilePath);
                        if (itemToSelect != null)
                        {
                            ResultsListBox.SelectedItem = itemToSelect;
                        }
                    }
                    
                    // Update cache
                    cachedFileVersions = currentFiles;
                }
            }
            catch
            {
                // Ignore errors during auto-refresh
            }
        }

        private void LoadResults()
        {
            LoadResultsInternal();
            
            // Update file cache on manual load
            cachedFileVersions.Clear();
            foreach (var result in allResults)
            {
                cachedFileVersions[result.FilePath] = GetFileVersion(result.FilePath);
            }
        }

        private static string GetFileVersion(string path)
        {
            var file = new FileInfo(path);
            return file.LastWriteTimeUtc.Ticks + ":" + file.Length;
        }
        
        private void LoadResultsInternal()
        {
            var results = new List<ResultItem>();

            try
            {
                // Load from goods folder
                if (Directory.Exists(goodsFolder))
                {
                    var goodsFiles = Directory.GetFiles(goodsFolder, "*.json");
                    foreach (var file in goodsFiles)
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        var stcpPath = Path.Combine(projectsDir, id + ".stcp");
                        results.Add(new ResultItem
                        {
                            ProjectId = $"Project {id}",
                            Status = "✓ Good",
                            ProjectFilePath = stcpPath,
                            FilePath = file,
                            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF027F0F")),
                            Category = "Good"
                        });
                    }
                }

                // Load from calc_fail folder
                if (Directory.Exists(calcFailFolder))
                {
                    var calcFailFiles = Directory.GetFiles(calcFailFolder, "*.json");
                    foreach (var file in calcFailFiles)
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        var stcpPath = Path.Combine(projectsDir, id + ".stcp");
                        results.Add(new ResultItem
                        {
                            ProjectId = $"Project {id}",
                            Status = "✗ Calculation Failed",
                            ProjectFilePath = stcpPath,
                            FilePath = file,
                            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFC0606")),
                            Category = "CalcFail"
                        });
                    }
                }

                // Load from sim_fail folder
                if (Directory.Exists(simFailFolder))
                {
                    var simFailFiles = Directory.GetFiles(simFailFolder, "*.json");
                    foreach (var file in simFailFiles)
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        var stcpPath = Path.Combine(projectsDir, id + ".stcp");
                        results.Add(new ResultItem
                        {
                            ProjectId = $"Project {id}",
                            Status = "⚠ Simulation Failed",
                            ProjectFilePath = stcpPath,
                            FilePath = file,
                            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD6B102")),
                            Category = "SimFail"
                        });
                    }
                }

                foreach (var result in results)
                {
                    AddRetryStatus(result);
                }

                // Sort by project ID (descending - newest first)
                results = results.OrderByDescending(r => 
                {
                    var fileName = Path.GetFileNameWithoutExtension(r.FilePath);
                    if (int.TryParse(fileName, out int id))
                        return id;
                    return 0;
                }).ToList();

                allResults = results;
                ApplyFilter(results);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading results: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyFilter(List<ResultItem> results)
        {
            // Save current selection
            string selectedFilePath = null;
            if (ResultsListBox.SelectedItem is ResultItem selected)
            {
                selectedFilePath = selected.FilePath;
            }
            
            List<ResultItem> filtered;
            if (currentFilter == "All")
            {
                filtered = results;
            }
            else
            {
                filtered = results.Where(r => r.Category == currentFilter).ToList();
            }

            ResultsListBox.ItemsSource = filtered;
            StatusTextBlock.Text = $"Showing {filtered.Count} result(s)";
            
            // Restore selection
            if (selectedFilePath != null)
            {
                var itemToSelect = filtered.FirstOrDefault(r => r.FilePath == selectedFilePath);
                if (itemToSelect != null)
                {
                    ResultsListBox.SelectedItem = itemToSelect;
                }
            }
        }

        private void FilterBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string filter)
            {
                currentFilter = filter;
                
                // Reset button backgrounds
                AllBtn.Background = Brushes.Transparent;
                GoodBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19027F0F"));
                CalcFailBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19FC0606"));
                SimFailBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19D6B102"));

                // Highlight selected button
                btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD0D0D0"));

                LoadResults();
            }
        }

        private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsListBox.SelectedItem is ResultItem selected)
            {
                LoadResultDetails(selected.FilePath);
            }
        }

        private static void AddRetryStatus(ResultItem result)
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(result.FilePath));
                var classification = ResultClassification.Read(json);
                result.Category = classification.Category;
                result.Status = classification.Status;
                result.StatusColor = classification.Category == "Good" ? Brushes.ForestGreen :
                    classification.Outcome == "Inconclusive" ? Brushes.DarkOrange : Brushes.DarkRed;
                if (classification.RecoveredAfterError) return;
                switch (json["RetryOutcome"]?.ToString())
                {
                    case "PassedOnRetry":
                        result.Status += " / Passed on retry";
                        break;
                    case "FailedAgain":
                        result.Status += " / Failed twice";
                        break;
                    case "RetryUnavailable":
                        result.Status += " / Retry unavailable";
                        break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (Newtonsoft.Json.JsonException) { }
        }

        private void LoadResultDetails(string filePath)
        {
            DetailsPanel.Children.Clear();

            try
            {
                if (!File.Exists(filePath))
                {
                    DetailsPanel.Children.Add(new TextBlock 
                    { 
                        Text = "File not found", 
                        Foreground = Brushes.Red,
                        Margin = new Thickness(0, 10, 0, 0)
                    });
                    return;
                }

                var jsonText = File.ReadAllText(filePath);
                var json = JObject.Parse(jsonText);
                var classification = ResultClassification.Read(json);

                // Project ID and Open button
                var projectId = Path.GetFileNameWithoutExtension(filePath);
                var stcpPath = Path.Combine(projectsDir, projectId + ".stcp");
                AddDetailHeaderWithOpenButton($"Project {projectId}", stcpPath);

                if (classification.RecoveredAfterError)
                    AddRetryOutcome("RecoveredAfterError");
                else
                    AddRetryOutcome(json["RetryOutcome"]?.ToString());
                AddDetailRow("Outcome", classification.Status);
                AddArtifactButton("Open diagnostic folder", json["DiagnosticsDirectory"]?.ToString(), true);

                // Show problematic operations summary first
                if (classification.IsTested && HasOperationResults(classification.Results))
                {
                    AddProblematicOperationsSummary(classification.Results);
                }

                // Basic Info
                AddDetailSection("Basic Information");
                AddDetailRow("Is Tested", classification.IsTested ? "Yes" : "No");
                if (!string.IsNullOrWhiteSpace(json["RunId"]?.ToString()))
                {
                    AddDetailRow("Run", json["RunId"].ToString());
                }

                var attempts = json["Attempts"] as JArray;
                if (attempts != null && attempts.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(json["Error"]?.ToString()) ||
                        !string.IsNullOrWhiteSpace(json["FailedStage"]?.ToString()))
                        DisplayAttemptFailure(json);
                    foreach (var attempt in attempts.OfType<JObject>())
                    {
                        DisplayAttempt(attempt);
                    }
                }
                else
                {
                    DisplayAttemptFailure(json);
                    DisplayOperationResults(json);
                }

                // Additional info if available
                if (json["ProjectName"] != null)
                {
                    AddDetailRow("Project Name", json["ProjectName"].ToString());
                }
            }
            catch (Exception ex)
            {
                DetailsPanel.Children.Add(new TextBlock 
                { 
                    Text = $"Error loading details: {ex.Message}", 
                    Foreground = Brushes.Red,
                    Margin = new Thickness(0, 10, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        private void AddRetryOutcome(string outcome)
        {
            string message;
            switch (outcome)
            {
                case "RecoveredAfterError":
                    message = "Passed on retry. The first opening/communication error is retained below.";
                    break;
                case "PassedOnRetry":
                    message = "Passed on retry (initial failure retained)";
                    break;
                case "FailedAgain":
                    message = "Failed twice with the same settings";
                    break;
                case "RetryUnavailable":
                    message = "Retry could not be completed; the initial failure is retained";
                    break;
                default:
                    return;
            }

            DetailsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 245, 219)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(128, 80, 0))
                }
            });
        }

        private void DisplayAttempt(JObject attempt)
        {
            var number = attempt["Attempt"]?.ToString() ?? "?";
            var passed = GetBoolValue(attempt, "Passed") == "Yes";
            var incomplete = !passed && ResultClassification.IsFalse(attempt["ValidationComplete"]) &&
                !ResultClassification.HasObservedErrors(attempt["Results"]) && !ResultClassification.HasStageErrors(attempt);
            AddDetailSection($"Attempt {number} - {(passed ? "Passed" : incomplete ? "Check incomplete" : "Failed")}");
            AddDetailRow("Is Tested", GetBoolValue(attempt, "IsTested"));
            if (attempt["StartedAtUtc"] != null)
                AddDetailRow("Started (UTC)", attempt["StartedAtUtc"].ToString());
            if (attempt["DurationMs"] != null)
                AddDetailRow("Duration (ms)", attempt["DurationMs"].ToString());

            DisplayAttemptFailure(attempt);
            if (attempt["ValidationComplete"] != null)
                AddDetailRow("Validation complete", GetBoolValue(attempt, "ValidationComplete"));
            if (!string.IsNullOrWhiteSpace(attempt["ValidationNote"]?.ToString()))
                AddDiagnosticMessage(attempt["ValidationNote"].ToString());
            var videoPath = attempt["VideoPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(videoPath))
            {
                if (!AddArtifactButton($"Open attempt {number} video", videoPath, false))
                    AddDiagnosticMessage("The video is unavailable in the local diagnostic folder.");
            }
            var recordingError = attempt["RecordingError"]?.ToString();
            if (!string.IsNullOrWhiteSpace(recordingError))
                AddDiagnosticMessage("Screen recording: " + recordingError);
            foreach (var warning in attempt["RecordingWarnings"] as JArray ?? [])
                DetailsPanel.Children.Add(new TextBlock
                {
                    Text = "Recording warning: " + warning,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.DarkOrange,
                    Margin = new Thickness(0, 6, 0, 8)
                });

            DisplayOperationResults(attempt);
            DisplayStageSnapshots(attempt);
        }

        private void DisplayStageSnapshots(JObject attempt)
        {
            foreach (var stage in (attempt["Stages"] as JArray ?? []).OfType<JObject>())
            {
                var panel = new StackPanel();
                var stageName = stage["Stage"]?.ToString() ?? "Stage";
                panel.Children.Add(new TextBlock
                {
                    Text = "Completed: " + GetBoolValue(stage, "IsComplete") +
                        "    Captured: " + stage["CapturedAtUtc"],
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrWhiteSpace(stage["Note"]?.ToString()))
                    panel.Children.Add(new TextBlock { Text = stage["Note"].ToString(), TextWrapping = TextWrapping.Wrap });
                foreach (var node in ResultClassification.EnabledNodes(stage["Results"]))
                {
                    var errors = ResultClassification.ErrorFields.Where(field => ResultClassification.IsTrue(node[field])).ToArray();
                    panel.Children.Add(new TextBlock
                    {
                        Text = (node["Name"]?.ToString() ?? "Operation") + ": calculated=" + GetBoolValue(node, "IsCalculated") +
                            ", simulated=" + GetBoolValue(node, "IsSimulated") + ", machining result=" + GetBoolValue(node, "IsMachiningResultCalculated") +
                            (errors.Length == 0 ? "; no reported errors" : "; " + string.Join(", ", errors)),
                        Foreground = errors.Length == 0 ? Brushes.DimGray : Brushes.DarkRed,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }
                DetailsPanel.Children.Add(new Expander
                {
                    Header = "Stage: " + stageName,
                    IsExpanded = false,
                    Content = panel,
                    Margin = new Thickness(0, 8, 0, 4)
                });
            }
        }

        private void DisplayAttemptFailure(JToken attempt)
        {
            var stage = attempt["FailedStage"]?.ToString();
            var error = attempt["Error"]?.ToString();
            var failureKind = attempt["FailureKind"]?.ToString();
            if (!string.IsNullOrWhiteSpace(failureKind) && failureKind != "None")
                AddDetailRow("Failure type", failureKind);
            else if (GetBoolValue(attempt, "IsTested") == "No" && !string.IsNullOrWhiteSpace(error))
                AddDetailRow("Failure type", "Opening/communication or execution error");
            if (!string.IsNullOrWhiteSpace(stage))
                AddDetailRow("Failed stage", stage, valueColor: Brushes.DarkRed);
            if (!string.IsNullOrWhiteSpace(error))
                AddDiagnosticMessage(error);
            else if (GetBoolValue(attempt, "IsTested") == "No")
                AddDiagnosticMessage("The test did not complete; no operation results were collected.");
        }

        private void AddDiagnosticMessage(string message)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DarkRed,
                Margin = new Thickness(0, 6, 0, 8)
            });
        }

        private static bool HasOperationResults(JToken results)
        {
            return results is JObject operation && operation.HasValues;
        }

        private void DisplayOperationResults(JToken attempt)
        {
            var results = attempt["Results"];
            if (GetBoolValue(attempt, "IsTested") == "No" || !HasOperationResults(results))
                return;

            var name = results["Name"]?.ToString();
            AddDetailSection(string.IsNullOrEmpty(name) ? "Results" : $"Results - {name}");
            DisplayResults(results, 0);
        }

        private bool AddArtifactButton(string caption, string relativePath, bool directory)
        {
            if (ResolveArtifactPath(relativePath, directory) == null)
                return false;

            var button = new Button
            {
                Content = caption,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 6, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand
            };
            button.Click += (sender, args) =>
            {
                try
                {
                    var path = ResolveArtifactPath(relativePath, directory);
                    if (path == null)
                    {
                        MessageBox.Show("The diagnostic file or folder is no longer available.", "Diagnostics",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open diagnostics: {ex.Message}", "Diagnostics",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            DetailsPanel.Children.Add(button);
            return true;
        }

        private static string ResolveArtifactPath(string relativePath, bool directory)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
                return null;

            try
            {
                var root = Path.GetFullPath(AppPaths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var path = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (directory ? !Directory.Exists(path) : !File.Exists(path))
                    return null;
                if (!directory && !string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Reject links/junctions so a relative path cannot escape the data folder.
                for (var current = path; current != null; current = Path.GetDirectoryName(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return null;
                    if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                        return path;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return null;
        }

        private void DisplayResults(JToken results, int indent)
        {
            if (!ResultClassification.IsEnabled(results)) return;

            var margin = new Thickness(indent * 20, 5, 0, 5);
            var successColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF027F0F"));
            var errorColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFC0606"));

            // Show operation name if it exists
            var name = results["Name"]?.ToString();
            if (!string.IsNullOrEmpty(name) && indent > 0)
            {
                AddOperationHeader(name, margin, HasAnyError(results));
            }

            // Status fields
            var calculated = GetBoolValue(results, "IsCalculated");
            var simulated = GetBoolValue(results, "IsSimulated");
            var machiningCalculated = GetBoolValue(results, "IsMachiningResultCalculated");
            
            AddDetailRow(calculated == "Yes" ? "✓ Calculated" : "✗ Calculated", calculated, margin, 
                calculated == "Yes" ? successColor : errorColor);
            AddDetailRow(simulated == "Yes" ? "✓ Simulated" : "✗ Simulated", simulated, margin, 
                simulated == "Yes" ? successColor : errorColor);
            AddDetailRow(machiningCalculated == "Yes" ? "✓ Machining Result Calculated" : "✗ Machining Result Calculated", 
                machiningCalculated, margin, machiningCalculated == "Yes" ? successColor : null);

            // Error fields - only show if they are true
            if (GetBoolValue(results, "IsError") == "Yes")
            {
                AddDetailRow("✗ General Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsCollisionError") == "Yes")
            {
                AddDetailRow("✗ Collision Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsCompensationError") == "Yes")
            {
                AddDetailRow("✗ Compensation Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsGougeError") == "Yes")
            {
                AddDetailRow("✗ Gouge Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsHolderError") == "Yes")
            {
                AddDetailRow("✗ Holder Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsPlungeError") == "Yes")
            {
                AddDetailRow("✗ Plunge Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsRapidError") == "Yes")
            {
                AddDetailRow("✗ Rapid Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsToolOverloadError") == "Yes")
            {
                AddDetailRow("✗ Tool Overload Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsTravelError") == "Yes")
            {
                AddDetailRow("✗ Travel Error", "Yes", margin, errorColor);
            }
            if (GetBoolValue(results, "IsTurnDirectionError") == "Yes")
            {
                AddDetailRow("✗ Turn Direction Error", "Yes", margin, errorColor);
            }

            // Children
            if (results["Children"] != null && results["Children"].Type == JTokenType.Array)
            {
                var children = results["Children"] as JArray;
                if (children != null && children.Count > 0)
                {
                    AddDetailSubHeader($"Operations ({children.Count})", margin);
                    foreach (var child in children)
                    {
                        DisplayResults(child, indent + 1);
                    }
                }
            }
        }

        private bool HasAnyError(JToken results)
        {
            if (!ResultClassification.IsEnabled(results)) return false;
            
            // Check if not calculated or not simulated - these are errors too
            if (GetBoolValue(results, "IsCalculated") == "No") return true;
            if (GetBoolValue(results, "IsSimulated") == "No") return true;
            
            string[] errorFields = { "IsError", "IsCollisionError", "IsCompensationError", "IsGougeError", 
                                     "IsHolderError", "IsPlungeError", "IsRapidError", "IsToolOverloadError", 
                                     "IsTravelError", "IsTurnDirectionError" };
            
            foreach (var field in errorFields)
            {
                if (GetBoolValue(results, field) == "Yes") return true;
            }
            return false;
        }

        private List<(string Name, List<string> Errors)> CollectProblematicOperations(JToken results)
        {
            var problems = new List<(string Name, List<string> Errors)>();
            CollectProblemsRecursive(results, problems);
            return problems;
        }

        private void CollectProblemsRecursive(JToken results, List<(string Name, List<string> Errors)> problems)
        {
            if (!ResultClassification.IsEnabled(results)) return;

            var name = results["Name"]?.ToString() ?? "Unknown";
            var errors = new List<string>();

            // Not calculated or not simulated are critical errors
            if (GetBoolValue(results, "IsCalculated") == "No") errors.Add("Not Calculated");
            if (GetBoolValue(results, "IsSimulated") == "No") errors.Add("Not Simulated");
            
            if (GetBoolValue(results, "IsError") == "Yes") errors.Add("General Error");
            if (GetBoolValue(results, "IsCollisionError") == "Yes") errors.Add("Collision");
            if (GetBoolValue(results, "IsCompensationError") == "Yes") errors.Add("Compensation");
            if (GetBoolValue(results, "IsGougeError") == "Yes") errors.Add("Gouge");
            if (GetBoolValue(results, "IsHolderError") == "Yes") errors.Add("Holder");
            if (GetBoolValue(results, "IsPlungeError") == "Yes") errors.Add("Plunge");
            if (GetBoolValue(results, "IsRapidError") == "Yes") errors.Add("Rapid");
            if (GetBoolValue(results, "IsToolOverloadError") == "Yes") errors.Add("Tool Overload");
            if (GetBoolValue(results, "IsTravelError") == "Yes") errors.Add("Travel");
            if (GetBoolValue(results, "IsTurnDirectionError") == "Yes") errors.Add("Turn Direction");

            if (errors.Count > 0)
            {
                problems.Add((name, errors));
            }

            // Check children
            if (results["Children"] != null && results["Children"].Type == JTokenType.Array)
            {
                var children = results["Children"] as JArray;
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        CollectProblemsRecursive(child, problems);
                    }
                }
            }
        }

        private void AddProblematicOperationsSummary(JToken results)
        {
            var problems = CollectProblematicOperations(results);
            if (problems.Count == 0) return;

            var errorColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFC0606"));
            var warningBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFEF3F2"));

            var summaryBorder = new Border
            {
                Background = warningBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var summaryPanel = new StackPanel();

            var summaryHeader = new TextBlock
            {
                Text = $"⚠ {problems.Count} Operation(s) with Errors",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = errorColor,
                Margin = new Thickness(0, 0, 0, 8)
            };
            summaryPanel.Children.Add(summaryHeader);

            foreach (var (opName, errors) in problems)
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                
                var nameBlock = new TextBlock
                {
                    Text = $"• {opName}: ",
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF333333"))
                };
                itemPanel.Children.Add(nameBlock);

                var errorsBlock = new TextBlock
                {
                    Text = string.Join(", ", errors),
                    Foreground = errorColor,
                    TextWrapping = TextWrapping.Wrap
                };
                itemPanel.Children.Add(errorsBlock);

                summaryPanel.Children.Add(itemPanel);
            }

            summaryBorder.Child = summaryPanel;
            DetailsPanel.Children.Add(summaryBorder);
        }

        private void AddOperationHeader(string name, Thickness margin, bool hasError)
        {
            var headerColor = hasError 
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFC0606"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF007AFF"));

            var header = new TextBlock
            {
                Text = (hasError ? "✗ " : "✓ ") + name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = headerColor,
                Margin = new Thickness(margin.Left, 12, 0, 4)
            };
            DetailsPanel.Children.Add(header);
        }

        private void AddDetailHeader(string text)
        {
            var header = new TextBlock
            {
                Text = text,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DetailsPanel.Children.Add(header);
        }

        private void AddDetailHeaderWithOpenButton(string text, string projectFilePath)
        {
            var headerPanel = new Grid();
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerPanel.Margin = new Thickness(0, 0, 0, 10);

            var header = new TextBlock
            {
                Text = text,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(header, 0);
            headerPanel.Children.Add(header);

            var openBtn = new Button
            {
                Content = "Open",
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7A5AF8")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = projectFilePath,
                IsEnabled = !isTestingRunning,
                ToolTip = isTestingRunning ? "Cannot open project while testing is running" : "Open project in ENCY"
            };
            openBtn.Click += OpenProjectBtn_Click;
            
            // Apply rounded corners
            var btnBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = openBtn.Background,
                Child = new TextBlock 
                { 
                    Text = "Open", 
                    Foreground = Brushes.White, 
                    FontWeight = FontWeights.Medium,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = projectFilePath,
                ToolTip = isTestingRunning ? "Cannot open project while testing is running" : "Open project in ENCY",
                Opacity = isTestingRunning ? 0.5 : 1.0
            };
            if (!isTestingRunning)
            {
                btnBorder.MouseLeftButtonDown += (s, e) => OpenProjectInEncy(projectFilePath);
            }
            
            Grid.SetColumn(btnBorder, 1);
            headerPanel.Children.Add(btnBorder);

            DetailsPanel.Children.Add(headerPanel);
        }

        private void OpenProjectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string projectPath)
            {
                OpenProjectInEncy(projectPath);
            }
        }

        private void OpenProjectInEncy(string projectFilePath)
        {
            if (isTestingRunning)
            {
                MessageBox.Show("Cannot open project while testing is running.", "Information", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrEmpty(camExePath) || !File.Exists(camExePath))
            {
                MessageBox.Show("ENCY path is not configured or file not found.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(projectFilePath))
            {
                MessageBox.Show($"Project file not found: {projectFilePath}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(camExePath, $"\"{projectFilePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening project: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ProjectPath_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is ResultItem item)
            {
                var projectPath = item.ProjectFilePath;
                if (File.Exists(projectPath))
                {
                    // Open explorer and select the file
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{projectPath}\"");
                }
                else
                {
                    // Open the projects folder
                    if (Directory.Exists(projectsDir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", projectsDir);
                    }
                    else
                    {
                        MessageBox.Show($"Project file not found: {projectPath}", "File Not Found", 
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                e.Handled = true;
            }
        }

        private void AddDetailSection(string text)
        {
            var section = new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 15, 0, 5),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF333333"))
            };
            DetailsPanel.Children.Add(section);
        }

        private void AddDetailSubHeader(string text, Thickness margin)
        {
            var subHeader = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = margin,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF666666"))
            };
            DetailsPanel.Children.Add(subHeader);
        }

        private void AddDetailRow(string label, string value, Thickness? margin = null, Brush valueColor = null)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE0E0E0")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 5, 0, 5),
                Margin = margin ?? new Thickness(0, 0, 0, 0)
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            var labelBlock = new TextBlock
            {
                Text = label + ":",
                FontWeight = FontWeights.SemiBold,
                Width = 220,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF555555"))
            };
            panel.Children.Add(labelBlock);

            var valueBlock = new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                Foreground = valueColor ?? Brushes.Black,
                FontWeight = valueColor != null ? FontWeights.Bold : FontWeights.Normal
            };
            panel.Children.Add(valueBlock);

            border.Child = panel;
            DetailsPanel.Children.Add(border);
        }

        private string GetBoolValue(JToken token, string propertyName)
        {
            var value = token[propertyName];
            if (value == null) return "N/A";
            if (value.Type == JTokenType.Boolean)
                return value.Value<bool>() ? "Yes" : "No";
            return value.ToString();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            LoadResults();
            DetailsPanel.Children.Clear();
            DetailsPanel.Children.Add(new TextBlock 
            { 
                Text = "Select a result to view details", 
                FontSize = 14, 
                Foreground = Brushes.Gray, 
                HorizontalAlignment = HorizontalAlignment.Center, 
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 50, 0, 0)
            });
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }
    }
}
