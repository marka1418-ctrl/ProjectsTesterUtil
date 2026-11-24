using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace ProjectTesterMonitor
{
    public partial class SettingsWindow : Window
    {
        private string settingsFilePath;
        private string dataDir;
        
        // Tag collections for each filter
        private List<string> operationsTags = new List<string>();
        private List<string> toolTypesTags = new List<string>();
        private List<string> axesTags = new List<string>();
        private List<string> tagsTags = new List<string>();

        public SettingsWindow()
        {
            InitializeComponent();
            
            dataDir = AppPaths.Root;
            settingsFilePath = Path.Combine(dataDir, "settings.json");
            
            // Add validation for numeric fields
            LaunchCamTimeout.PreviewTextInput += NumericTextBox_PreviewTextInput;
            OpenProjectTimeout.PreviewTextInput += NumericTextBox_PreviewTextInput;
            TestProjectTimeout.PreviewTextInput += NumericTextBox_PreviewTextInput;
            KillCamProcessTimeout.PreviewTextInput += NumericTextBox_PreviewTextInput;
            OwnerTextBox.PreviewTextInput += NumericTextBox_PreviewTextInput;
            CountOfTestedProjectsBeforeRestart.PreviewTextInput += NumericTextBox_PreviewTextInput;
            
            // Initialize placeholder text for filter inputs
            InitializePlaceholders();
            
            LoadSettings();
        }
        
        private void InitializePlaceholders()
        {
            SetPlaceholder(OperationsInput);
            SetPlaceholder(ToolTypesInput);
            SetPlaceholder(AxesInput);
            SetPlaceholder(TagsInput);
        }
        
        private void SetPlaceholder(TextBox textBox)
        {
            if (string.IsNullOrEmpty(textBox.Text))
            {
                textBox.Text = textBox.Tag?.ToString() ?? "";
                textBox.Foreground = Brushes.Gray;
                textBox.FontStyle = FontStyles.Italic;
            }
        }
        
        private void FilterInput_GotFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null && textBox.Text == textBox.Tag?.ToString())
            {
                textBox.Text = "";
                textBox.Foreground = Brushes.Black;
                textBox.FontStyle = FontStyles.Normal;
            }
        }
        
        private void FilterInput_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null && string.IsNullOrEmpty(textBox.Text))
            {
                SetPlaceholder(textBox);
            }
        }

        private void NumericTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // Allow only digits
            e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(e.Text, "^[0-9]+$");
        }
        
        #region Tag Management
        
        private Border CreateTagElement(string text, List<string> tagList, WrapPanel panel)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x6B, 0x8E, 0x23)), // Olive green
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(8, 4, 4, 4)
            };
            
            var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };
            
            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            
            var closeButton = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                FontSize = 10,
                Cursor = Cursors.Hand,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = new Tuple<string, List<string>, WrapPanel, Border>(text, tagList, panel, border)
            };
            
            closeButton.Click += RemoveTag_Click;
            
            // Hover effect for close button
            closeButton.MouseEnter += (s, e) => closeButton.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0xCC));
            closeButton.MouseLeave += (s, e) => closeButton.Foreground = Brushes.White;
            
            stackPanel.Children.Add(textBlock);
            stackPanel.Children.Add(closeButton);
            border.Child = stackPanel;
            
            return border;
        }
        
        private void RemoveTag_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.Tag is Tuple<string, List<string>, WrapPanel, Border> tagInfo)
            {
                tagInfo.Item2.Remove(tagInfo.Item1); // Remove from list
                tagInfo.Item3.Children.Remove(tagInfo.Item4); // Remove from panel
            }
        }
        
        private void AddTag(string text, List<string> tagList, WrapPanel panel, TextBox inputBox)
        {
            // Support adding multiple tags separated by comma
            var items = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var item in items)
            {
                var trimmedText = item.Trim();
                if (string.IsNullOrEmpty(trimmedText) || tagList.Contains(trimmedText))
                {
                    continue;
                }
                
                tagList.Add(trimmedText);
                var tagElement = CreateTagElement(trimmedText, tagList, panel);
                panel.Children.Add(tagElement);
            }
            
            inputBox.Clear();
        }
        
        private void LoadTagsToPanel(List<string> tagList, WrapPanel panel)
        {
            panel.Children.Clear();
            foreach (var tag in tagList)
            {
                var tagElement = CreateTagElement(tag, tagList, panel);
                panel.Children.Add(tagElement);
            }
        }
        
        // KeyDown handlers for each filter input
        private void OperationsInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddTag(OperationsInput.Text, operationsTags, OperationsPanel, OperationsInput);
                e.Handled = true;
            }
        }
        
        private void ToolTypesInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddTag(ToolTypesInput.Text, toolTypesTags, ToolTypesPanel, ToolTypesInput);
                e.Handled = true;
            }
        }
        
        private void AxesInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddTag(AxesInput.Text, axesTags, AxesPanel, AxesInput);
                e.Handled = true;
            }
        }
        
        private void TagsInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddTag(TagsInput.Text, tagsTags, TagsPanel, TagsInput);
                e.Handled = true;
            }
        }
        
        #endregion

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsFilePath))
                {
                    StatusTextBlock.Text = "Settings file not found. Using defaults.";
                    SetDefaultValues();
                    return;
                }

                var jsonText = File.ReadAllText(settingsFilePath);
                var json = JObject.Parse(jsonText);
                LoadSettingsFromJson(json);
                StatusTextBlock.Text = "Settings loaded successfully.";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error loading settings: {ex.Message}";
                MessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetDefaultValues();
            }
        }

        private void LoadSettingsFromJson(JObject json)
        {
            // Timeouts
            LaunchCamTimeout.Text = GetValue(json, "LaunchCamTimeoutInSeconds", "20");
            OpenProjectTimeout.Text = GetValue(json, "OpenProjectTimeoutInSeconds", "900");
            TestProjectTimeout.Text = GetValue(json, "TestProjectTimeoutInSeconds", "3600");
            KillCamProcessTimeout.Text = GetValue(json, "KillCamProcessTimeoutInSeconds", "300");

            // Options
            UseJobMode.IsChecked = GetBoolValue(json, "UseJobMode", true);
            ValidateBackgroundSimulation.IsChecked = GetBoolValue(json, "ValidateBackgroundSimulation", false);
            KeepSuccessfulRecordings.IsChecked = GetBoolValue(json, "KeepSuccessfulRecordings", false);
            OnlyRecommended.IsChecked = GetBoolValue(json, "OnlyRecommended", true);
            OnlyPublic.IsChecked = GetBoolValue(json, "OnlyPublic", true);

            // Simulation
            var speedPercent = GetIntValue(json, "SimulationSpeedPercent", 100);
            SimulationSpeedSlider.Value = speedPercent;
            SimulationSpeedValue.Text = speedPercent.ToString();
            CheckGouges.IsChecked = GetBoolValue(json, "CheckGouges", true);
            CheckHolderCollisions.IsChecked = GetBoolValue(json, "CheckHolderCollisions", true);
            CheckMachineCollisions.IsChecked = GetBoolValue(json, "CheckMachineCollisions", true);
            BreakOnStopCommand.IsChecked = GetBoolValue(json, "BreakOnStopCommand", false);
            BreakOnEndOfOperation.IsChecked = GetBoolValue(json, "BreakOnEndOfOperation", false);
            BreakOnErrors.IsChecked = GetBoolValue(json, "BreakOnErrors", false);

            // Connection
            BaseUrl.Text = GetValue(json, "BaseUrl", "https://webservices.sprutcam.com");
            Login.Text = GetValue(json, "Login", "");
            Password.Password = GetValue(json, "Password", "");

            // Machine
            MachineType.Text = GetValue(json, "MachineType", "");
            MachineName.Text = GetValue(json, "MachineName", "");
            OwnerTextBox.Text = GetValue(json, "Owner", "0");

            // Other
            BinDataVersion.Text = GetValue(json, "BinDataVersion", "1999");
            CountOfTestedProjectsBeforeRestart.Text = GetValue(json, "CountOfTestedProjectsBeforeRestart", "1");

            // Arrays - Load as tags
            operationsTags = GetArrayAsList(json, "Operations");
            toolTypesTags = GetArrayAsList(json, "ToolTypes");
            axesTags = GetArrayAsList(json, "Axes");
            tagsTags = GetArrayAsList(json, "Tags");
            
            // Refresh tag panels
            LoadTagsToPanel(operationsTags, OperationsPanel);
            LoadTagsToPanel(toolTypesTags, ToolTypesPanel);
            LoadTagsToPanel(axesTags, AxesPanel);
            LoadTagsToPanel(tagsTags, TagsPanel);
        }

        private void SetDefaultValues()
        {
            LaunchCamTimeout.Text = "20";
            OpenProjectTimeout.Text = "900";
            TestProjectTimeout.Text = "3600";
            KillCamProcessTimeout.Text = "300";
            UseJobMode.IsChecked = true;
            ValidateBackgroundSimulation.IsChecked = false;
            KeepSuccessfulRecordings.IsChecked = false;
            OnlyRecommended.IsChecked = true;
            OnlyPublic.IsChecked = true;
            
            // Simulation defaults
            SimulationSpeedSlider.Value = 100;
            SimulationSpeedValue.Text = "100";
            CheckGouges.IsChecked = true;
            CheckHolderCollisions.IsChecked = true;
            CheckMachineCollisions.IsChecked = true;
            BreakOnStopCommand.IsChecked = false;
            BreakOnEndOfOperation.IsChecked = false;
            BreakOnErrors.IsChecked = false;
            
            BaseUrl.Text = "https://webservices.sprutcam.com";
            Login.Text = "";
            Password.Password = "";
            MachineType.Text = "";
            MachineName.Text = "";
            OwnerTextBox.Text = "0";
            BinDataVersion.Text = "1999";
            CountOfTestedProjectsBeforeRestart.Text = "1";
            operationsTags.Clear();
            toolTypesTags.Clear();
            axesTags.Clear();
            tagsTags.Clear();
            
            OperationsPanel.Children.Clear();
            ToolTypesPanel.Children.Clear();
            AxesPanel.Children.Clear();
            TagsPanel.Children.Clear();
        }

        private string GetValue(JObject json, string key, string defaultValue)
        {
            var token = json[key];
            return token != null ? token.ToString() : defaultValue;
        }

        private bool GetBoolValue(JObject json, string key, bool defaultValue)
        {
            var token = json[key];
            if (token == null) return defaultValue;
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            if (bool.TryParse(token.ToString(), out bool result))
                return result;
            return defaultValue;
        }

        private int GetIntValue(JObject json, string key, int defaultValue)
        {
            var token = json[key];
            if (token == null) return defaultValue;
            if (token.Type == JTokenType.Integer)
                return token.Value<int>();
            if (int.TryParse(token.ToString(), out int result))
                return result;
            return defaultValue;
        }

        private void SimulationSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SimulationSpeedValue != null)
            {
                SimulationSpeedValue.Text = ((int)e.NewValue).ToString();
            }
        }

        private string GetArrayValue(JObject json, string key)
        {
            var token = json[key];
            if (token == null || token.Type != JTokenType.Array)
                return "";
            
            var array = token as JArray;
            if (array == null || array.Count == 0)
                return "";
            
            return string.Join(", ", array.Select(item => item.ToString()));
        }
        
        private List<string> GetArrayAsList(JObject json, string key)
        {
            var token = json[key];
            if (token == null || token.Type != JTokenType.Array)
                return new List<string>();
            
            var array = token as JArray;
            if (array == null || array.Count == 0)
                return new List<string>();
            
            return array.Select(item => item.ToString()).ToList();
        }

        private void SaveSettings()
        {
            try
            {
                // Validate numeric fields
                if (!int.TryParse(LaunchCamTimeout.Text, out int launchCamTimeout))
                {
                    MessageBox.Show("Launch CAM Timeout must be a valid integer.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    LaunchCamTimeout.Focus();
                    return;
                }

                if (!int.TryParse(OpenProjectTimeout.Text, out int openProjectTimeout))
                {
                    MessageBox.Show("Open Project Timeout must be a valid integer.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OpenProjectTimeout.Focus();
                    return;
                }

                if (!int.TryParse(TestProjectTimeout.Text, out int testProjectTimeout))
                {
                    MessageBox.Show("Test Project Timeout must be a valid integer.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    TestProjectTimeout.Focus();
                    return;
                }

                if (!int.TryParse(KillCamProcessTimeout.Text, out int killCamProcessTimeout))
                {
                    MessageBox.Show("Kill CAM Process Timeout must be a valid integer.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    KillCamProcessTimeout.Focus();
                    return;
                }

                if (!int.TryParse(OwnerTextBox.Text, out int owner))
                {
                    MessageBox.Show("Owner must be a valid integer.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OwnerTextBox.Focus();
                    return;
                }

                if (!int.TryParse(CountOfTestedProjectsBeforeRestart.Text, out int countBeforeRestart))
                {
                    MessageBox.Show("Count Before Restart must be a valid integer.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    CountOfTestedProjectsBeforeRestart.Focus();
                    return;
                }

                // Create JSON object
                var json = new JObject
                {
                    ["LaunchCamTimeoutInSeconds"] = launchCamTimeout,
                    ["OpenProjectTimeoutInSeconds"] = openProjectTimeout,
                    ["TestProjectTimeoutInSeconds"] = testProjectTimeout,
                    ["KillCamProcessTimeoutInSeconds"] = killCamProcessTimeout,
                    ["UseJobMode"] = UseJobMode.IsChecked ?? false,
                    ["ValidateBackgroundSimulation"] = ValidateBackgroundSimulation.IsChecked ?? false,
                    ["KeepSuccessfulRecordings"] = KeepSuccessfulRecordings.IsChecked ?? false,
                    ["BinDataVersion"] = BinDataVersion.Text,
                    ["BaseUrl"] = BaseUrl.Text,
                    ["CountOfTestedProjectsBeforeRestart"] = countBeforeRestart,
                    ["Login"] = Login.Text,
                    ["Password"] = Password.Password,
                    ["OnlyRecommended"] = OnlyRecommended.IsChecked ?? false,
                    ["OnlyPublic"] = OnlyPublic.IsChecked ?? false,
                    ["MachineType"] = MachineType.Text,
                    ["MachineName"] = MachineName.Text,
                    ["Owner"] = owner,
                    // Simulation settings
                    ["SimulationSpeedPercent"] = (int)SimulationSpeedSlider.Value,
                    ["CheckGouges"] = CheckGouges.IsChecked ?? true,
                    ["CheckHolderCollisions"] = CheckHolderCollisions.IsChecked ?? true,
                    ["CheckMachineCollisions"] = CheckMachineCollisions.IsChecked ?? true,
                    ["BreakOnStopCommand"] = BreakOnStopCommand.IsChecked ?? false,
                    ["BreakOnEndOfOperation"] = BreakOnEndOfOperation.IsChecked ?? false,
                    ["BreakOnErrors"] = BreakOnErrors.IsChecked ?? false
                };

                // Parse arrays from tag lists
                json["Operations"] = new JArray(operationsTags);
                json["ToolTypes"] = new JArray(toolTypesTags);
                json["Axes"] = new JArray(axesTags);
                json["Tags"] = new JArray(tagsTags);

                // Save to file
                var jsonText = json.ToString(Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(settingsFilePath, jsonText);

                StatusTextBlock.Text = "Settings saved successfully.";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error saving settings: {ex.Message}";
                MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private JArray ParseArray(string text)
        {
            var array = new JArray();
            if (string.IsNullOrWhiteSpace(text))
                return array;

            var items = text.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(item => item.Trim())
                           .Where(item => !string.IsNullOrEmpty(item));

            foreach (var item in items)
            {
                array.Add(item);
            }

            return array;
        }

        private void LoadSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files|*.json|All files|*.*",
                Title = "Load Settings File",
                InitialDirectory = dataDir
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var loadedPath = openFileDialog.FileName;
                    var json = JObject.Parse(File.ReadAllText(loadedPath));
                    LoadSettingsFromJson(json);
                    StatusTextBlock.Text = "Settings loaded from file.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Helper method to create Apple-style button
        private Button CreateAppleButton(string content, bool isPrimary = false, bool isDanger = false)
        {
            var btn = new Button
            {
                Content = content,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0)
            };

            var normalBrush = isPrimary ? new SolidColorBrush(Color.FromRgb(122, 90, 248)) :
                              isDanger ? new SolidColorBrush(Color.FromRgb(255, 59, 48)) :
                              new SolidColorBrush(Color.FromRgb(246, 244, 251));
            var hoverBrush = isPrimary ? new SolidColorBrush(Color.FromRgb(107, 72, 232)) :
                             isDanger ? new SolidColorBrush(Color.FromRgb(230, 52, 43)) :
                             new SolidColorBrush(Color.FromRgb(231, 227, 242));
            var foreground = (isPrimary || isDanger) ? System.Windows.Media.Brushes.White : 
                             new SolidColorBrush(Color.FromRgb(32, 27, 51));

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            borderFactory.Name = "border";
            borderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, normalBrush);
            borderFactory.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(16, 10, 16, 10));
            
            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentPresenter);
            
            template.VisualTree = borderFactory;

            // Triggers for hover effect
            var mouseOverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, hoverBrush, "border"));
            template.Triggers.Add(mouseOverTrigger);

            btn.Template = template;
            btn.Foreground = foreground;

            return btn;
        }

        private void LoadPresetBtn_Click(object sender, RoutedEventArgs e)
        {
            // Built-in presets
            var builtInPresets = new[]
            {
                ("settings.json", "Default Projects"),
                ("settings_Recommended.json", "Recommended Projects"),
                ("settings_All_Public.json", "All Public Projects"),
                ("settings_TestSuite.json", "Test Suite Projects")
            };

            var availablePresets = builtInPresets
                .Where(p => File.Exists(Path.Combine(dataDir, p.Item1)))
                .ToList();

            // Find user presets (settings_User_*.json)
            try
            {
                var userPresetFiles = Directory.GetFiles(dataDir, "settings_User_*.json");
                foreach (var file in userPresetFiles)
                {
                    var fileName = Path.GetFileName(file);
                    var displayName = fileName
                        .Replace("settings_User_", "")
                        .Replace(".json", "")
                        .Replace("_", " ");
                    availablePresets.Add((fileName, displayName));
                }
            }
            catch { }

            if (availablePresets.Count == 0)
            {
                MessageBox.Show("No preset files found.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int dialogHeight = 140 + (availablePresets.Count * 52);
            if (dialogHeight > 500) dialogHeight = 500;

            Window dialog = null;
            dialog = new Window
            {
                Title = "Select Preset",
                Width = 340,
                Height = dialogHeight,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            
            dialog.KeyDown += (s, args) => { if (args.Key == Key.Escape) dialog.Close(); };

            var outerBorder = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(12),
                Background = System.Windows.Media.Brushes.White,
                Padding = new Thickness(20)
            };
            outerBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.15
            };

            var mainPanel = new StackPanel();
            
            var title = new TextBlock
            {
                Text = "Select Preset",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(32, 27, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            mainPanel.Children.Add(title);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = dialogHeight - 120
            };
            var stackPanel = new StackPanel();

            foreach (var (fileName, displayName) in availablePresets)
            {
                var isUserPreset = fileName.StartsWith("settings_User_");
                
                var btnGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (isUserPreset)
                {
                    btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }

                var btn = CreateAppleButton(displayName);
                btn.HorizontalAlignment = HorizontalAlignment.Stretch;
                btn.Tag = Path.Combine(dataDir, fileName);
                btn.Click += (s, args) =>
                {
                    try
                    {
                        settingsFilePath = btn.Tag.ToString();
                        LoadSettings();
                        StatusTextBlock.Text = $"Loaded: {displayName}";
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading preset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
                Grid.SetColumn(btn, 0);
                btnGrid.Children.Add(btn);

                if (isUserPreset)
                {
                    var deleteBtn = CreateAppleButton("×", isDanger: true);
                    deleteBtn.Margin = new Thickness(8, 0, 0, 0);
                    deleteBtn.Tag = Path.Combine(dataDir, fileName);
                    deleteBtn.ToolTip = "Delete preset";
                    deleteBtn.Click += (s, args) =>
                    {
                        var confirmed = ShowAppleConfirmDialog("Delete Preset", $"Are you sure you want to delete '{displayName}'?");
                        if (confirmed)
                        {
                            try
                            {
                                File.Delete(deleteBtn.Tag.ToString());
                                dialog.Close();
                                LoadPresetBtn_Click(sender, e);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Error deleting preset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                    };
                    Grid.SetColumn(deleteBtn, 1);
                    btnGrid.Children.Add(deleteBtn);
                }

                stackPanel.Children.Add(btnGrid);
            }

            scrollViewer.Content = stackPanel;
            mainPanel.Children.Add(scrollViewer);

            var closeBtn = CreateAppleButton("Cancel");
            closeBtn.Margin = new Thickness(0, 12, 0, 0);
            closeBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            closeBtn.Click += (s, args) => dialog.Close();
            mainPanel.Children.Add(closeBtn);

            outerBorder.Child = mainPanel;
            dialog.Content = outerBorder;
            dialog.ShowDialog();
        }

        private void SavePresetBtn_Click(object sender, RoutedEventArgs e)
        {
            Window dialog = null;
            dialog = new Window
            {
                Title = "Save Preset",
                Width = 340,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            
            dialog.KeyDown += (s, args) => { if (args.Key == Key.Escape) dialog.Close(); };

            var outerBorder = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(12),
                Background = System.Windows.Media.Brushes.White,
                Padding = new Thickness(20)
            };
            outerBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.15
            };

            var stackPanel = new StackPanel();

            var title = new TextBlock
            {
                Text = "Save Preset",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(32, 27, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            stackPanel.Children.Add(title);

            var inputLabel = new TextBlock
            {
                Text = "PRESET NAME",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(131, 125, 150)),
                Margin = new Thickness(0, 0, 0, 6)
            };
            stackPanel.Children.Add(inputLabel);

            // Apple-style TextBox
            var inputBorder = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromRgb(246, 244, 251)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 16)
            };
            var presetNameInput = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                FontSize = 14,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            inputBorder.Child = presetNameInput;
            stackPanel.Children.Add(inputBorder);

            var buttonsPanel = new Grid();
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cancelBtn = CreateAppleButton("Cancel");
            cancelBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            cancelBtn.Click += (s, args) => dialog.Close();
            Grid.SetColumn(cancelBtn, 0);
            buttonsPanel.Children.Add(cancelBtn);

            var saveBtn = CreateAppleButton("Save", isPrimary: true);
            saveBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            saveBtn.Click += (s, args) =>
            {
                var presetName = presetNameInput.Text.Trim();
                if (string.IsNullOrEmpty(presetName))
                {
                    MessageBox.Show("Please enter a preset name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var invalidChars = Path.GetInvalidFileNameChars();
                foreach (var c in invalidChars)
                {
                    presetName = presetName.Replace(c, '_');
                }
                presetName = presetName.Replace(" ", "_");

                var fileName = $"settings_User_{presetName}.json";
                var filePath = Path.Combine(dataDir, fileName);

                if (File.Exists(filePath))
                {
                    var result = MessageBox.Show($"Preset '{presetName}' already exists. Overwrite?", "Confirm", 
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                try
                {
                    var originalPath = settingsFilePath;
                    settingsFilePath = filePath;
                    SaveSettings();
                    settingsFilePath = originalPath;
                    
                    StatusTextBlock.Text = $"Preset '{presetName}' saved.";
                    dialog.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving preset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            Grid.SetColumn(saveBtn, 2);
            buttonsPanel.Children.Add(saveBtn);

            stackPanel.Children.Add(buttonsPanel);

            outerBorder.Child = stackPanel;
            dialog.Content = outerBorder;
            
            dialog.Loaded += (s, args) => presetNameInput.Focus();
            
            dialog.ShowDialog();
        }

        /// <summary>
        /// Shows an Apple-style confirmation dialog
        /// </summary>
        private bool ShowAppleConfirmDialog(string title, string message, string confirmText = "Delete", string cancelText = "Cancel")
        {
            bool result = false;
            
            var confirmDialog = new Window
            {
                Title = title,
                Width = 340,
                Height = 160,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            
            confirmDialog.KeyDown += (s, args) => { if (args.Key == Key.Escape) confirmDialog.Close(); };

            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(231, 227, 242)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20)
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
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(32, 27, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            mainPanel.Children.Add(titleBlock);

            var messageBlock = new TextBlock
            {
                Text = message,
                FontSize = 13,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(131, 125, 150)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };
            mainPanel.Children.Add(messageBlock);

            var buttonsPanel = new Grid();
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cancelBtn = CreateAppleButton(cancelText);
            cancelBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            cancelBtn.Click += (s, args) => { result = false; confirmDialog.Close(); };
            Grid.SetColumn(cancelBtn, 0);
            buttonsPanel.Children.Add(cancelBtn);

            var confirmBtn = CreateAppleButton(confirmText, isDanger: true);
            confirmBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            confirmBtn.Click += (s, args) => { result = true; confirmDialog.Close(); };
            Grid.SetColumn(confirmBtn, 2);
            buttonsPanel.Children.Add(confirmBtn);

            mainPanel.Children.Add(buttonsPanel);

            outerBorder.Child = mainPanel;
            confirmDialog.Content = outerBorder;
            confirmDialog.ShowDialog();
            
            return result;
        }

        private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Close();
        }

        private void BackupSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files|*.json|All files|*.*",
                Title = "Backup Settings",
                InitialDirectory = dataDir,
                FileName = "settings_backup.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    // Temporarily change the path to save to the selected file
                    var originalPath = settingsFilePath;
                    settingsFilePath = saveFileDialog.FileName;
                    SaveSettings();
                    settingsFilePath = originalPath;
                    StatusTextBlock.Text = "Settings backed up successfully.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error backing up settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
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
