using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Trio.SharedLibrary;
using Trio.SharedLibrary.Functionalities;

namespace TrioAI.MPPlugIn
{
    internal class ChatPanel : UserControl, IToolControl
    {
        private readonly ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        private ScrollViewer _scrollViewer;
        private TextBox _inputBox;
        private Button _sendBtn;
        private AiService _ai;
        private bool _isProcessing;
        private DockPanel _inputPanel;
        private Border _confirmPanel;
        private TaskCompletionSource<bool> _confirmTcs;
        private static bool _aboutShown;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private ChatMessage _streamingMsg;
        private System.Threading.CancellationTokenSource _cts;
        private TextBlock _statusInfo;

        // --- Oscilloscope pattern: static factory ---
        private static readonly ToolPositionSettings _defaultPosition = new ToolPositionSettings(SizeToContent.Manual)
        {
            fwidth = 420.0,
            fheight = 560.0
        };

        private static readonly ChatPanelFactory _factory = new ChatPanelFactory(_defaultPosition);

        public static ToolFactory Factory => (ToolFactory)(object)_factory;
        public static string RegisteredToolName => _factory.ToolName;

        private readonly ChatTool _tool;

        public ITool Tool => (ITool)(object)_tool;
        public IInputElement DefaultFocusedElement => _inputBox;
        public string HelpKeyword => "TrioAI";

        public ChatPanel()
        {
            AiService.PerfLog("ChatPanel ctor: enter");
            try
            {
                _tool = new ChatTool(this);
                AiService.PerfLog("ChatPanel ctor: ChatTool created");
                InitAiService();
                AiService.PerfLog("ChatPanel ctor: InitAiService done");
                BuildUI();
                AiService.PerfLog("ChatPanel ctor: BuildUI done");
                _ai.StartNewSession();
                AiService.PerfLog("ChatPanel ctor: StartNewSession done");
                this.Loaded += (s, e) =>
                {
                    AiService.PerfLog("ChatPanel: Loaded event fired (rendered)");
                    LoadLastSession();
                    AiService.PerfLog("ChatPanel: LoadLastSession done (post-Loaded)");
                    if (!_aboutShown)
                    {
                        _aboutShown = true;
                        OnAbout(null, null);
                    }
                    AiService.PerfLogFlush();
                };
            }
            catch (Exception ex)
            {
                AiService.PerfLog("ChatPanel ctor THREW: " + ex.GetType().Name + ": " + ex.Message);
                try { AiService.LogException("ChatPanel ctor", ex); } catch { }
                throw;
            }
            finally
            {
                AiService.PerfLogFlush();
            }
        }

        private void LoadLastSession()
        {
            try
            {
                AiService.PerfLog("LoadLastSession: enter");
                var sessions = _ai.ListSessions();
                AiService.PerfLog("LoadLastSession: ListSessions done, count=" + sessions.Count);
                if (sessions.Count == 0) return;
                var last = sessions[0]; // already sorted by time descending
                var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrioAI");
                var path = Path.Combine(dataDir, "chat_history", last.Id + ".json");
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path);
                var data = _json.Deserialize<Dictionary<string, object>>(text);
                AiService.PerfLog("LoadLastSession: file parsed");
                _ai.LoadSession(last.Id);
                object msgsObj;
                if (data.TryGetValue("messages", out msgsObj) && msgsObj is System.Collections.ArrayList al)
                {
                    foreach (var m in al)
                    {
                        var md = m as Dictionary<string, object>;
                        if (md != null)
                        {
                            var role = md.ContainsKey("role") ? md["role"]?.ToString() : "System";
                            var msg = md.ContainsKey("text") ? md["text"]?.ToString() : "";
                            _messages.Add(new ChatMessage(role, msg));
                        }
                    }
                }
                AiService.PerfLog("LoadLastSession: messages added");
                Dispatcher.BeginInvoke(new Action(() => _scrollViewer.ScrollToEnd()));
            }
            catch { }
        }

        private void InitAiService()
        {
            _ai = new AiService();
            _ai.OnAiTextStart = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _streamingMsg = new ChatMessage("AI", "");
                    _messages.Add(_streamingMsg);
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnAiTextDelta = (delta) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_streamingMsg != null)
                    {
                        _streamingMsg.Text = (_streamingMsg.Text ?? "") + delta;
                        _scrollViewer.ScrollToEnd();
                    }
                }));
            };
            _ai.OnAiTextEnd = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_streamingMsg != null && string.IsNullOrEmpty(_streamingMsg.Text))
                    {
                        // Empty bubble — remove it
                        _messages.Remove(_streamingMsg);
                    }
                    _streamingMsg = null;
                    AutoSaveSession();
                }));
            };
            _ai.OnSystemMessage = (text) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _messages.Add(new ChatMessage("System", text));
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnToolStatus = (status) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _messages.Add(new ChatMessage("System", status));
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnConfirmWrite = (toolName, argsJson) =>
            {
                return ShowInlineConfirmation(toolName, argsJson);
            };
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private void BuildUI()
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));

            var root = new DockPanel();

            // ---- Top toolbar ----
            var toolbar = new DockPanel { Margin = new Thickness(4, 4, 8, 4), LastChildFill = false };
            toolbar.SetValue(DockPanel.DockProperty, Dock.Top);

            var settingsBtn = new Button
            {
                Content = Lang.S("Settings"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            settingsBtn.Click += OnSettings;
            DockPanel.SetDock(settingsBtn, Dock.Right);
            toolbar.Children.Add(settingsBtn);

            var historyBtn = new Button
            {
                Content = Lang.S("History"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            historyBtn.Click += OnHistory;
            DockPanel.SetDock(historyBtn, Dock.Right);
            toolbar.Children.Add(historyBtn);

            var clearBtn = new Button
            {
                Content = Lang.S("Clear"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            clearBtn.Click += (s, e) =>
            {
                _messages.Clear();
                _ai.StartNewSession();
                UpdateStatusInfo();
            };
            DockPanel.SetDock(clearBtn, Dock.Right);
            toolbar.Children.Add(clearBtn);

            var newBtn = new Button
            {
                Content = Lang.S("New"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, 90, 160)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            newBtn.Click += (s, e) =>
            {
                if (_messages.Count > 0) AutoSaveSession();
                _messages.Clear();
                _ai.StartNewSession();
                UpdateStatusInfo();
            };
            DockPanel.SetDock(newBtn, Dock.Right);
            toolbar.Children.Add(newBtn);

            _statusInfo = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            toolbar.Children.Add(_statusInfo);

            var aboutBtn = new Button
            {
                Content = "关于",
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            aboutBtn.Click += OnAbout;
            DockPanel.SetDock(aboutBtn, Dock.Left);
            toolbar.Children.Add(aboutBtn);

            root.Children.Add(toolbar);

            // Top separator
            var topSep = new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) };
            topSep.SetValue(DockPanel.DockProperty, Dock.Top);
            root.Children.Add(topSep);

            // ---- Bottom input panel ----
            _inputPanel = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
            _inputPanel.SetValue(DockPanel.DockProperty, Dock.Bottom);

            _sendBtn = new Button
            {
                Content = Lang.S("Send"),
                Width = 60,
                Height = 28,
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            _sendBtn.Click += OnSend;
            DockPanel.SetDock(_sendBtn, Dock.Right);
            _inputPanel.Children.Add(_sendBtn);

            _inputBox = new TextBox
            {
                Height = 80,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                Padding = new Thickness(6, 4, 6, 4)
            };
            _inputBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    OnSend(null, null);
                }
            };
            _inputPanel.Children.Add(_inputBox);
            root.Children.Add(_inputPanel);

            // ---- Confirm panel (hidden, shown when AI needs permission) ----
            _confirmPanel = new Border
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Color.FromRgb(50, 45, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 100, 30)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 4, 8, 4)
            };
            _confirmPanel.SetValue(DockPanel.DockProperty, Dock.Bottom);

            var confirmDock = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };

            var rejectBtn = new Button
            {
                Content = Lang.S("Reject"),
                Width = 65, Height = 24,
                Background = new SolidColorBrush(Color.FromRgb(160, 40, 40)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            rejectBtn.Click += OnConfirmReject;
            DockPanel.SetDock(rejectBtn, Dock.Left);
            confirmDock.Children.Add(rejectBtn);

            var allowBtn = new Button
            {
                Content = Lang.S("Allow"),
                Width = 65, Height = 24,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 70)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            allowBtn.Click += OnConfirmAllow;
            DockPanel.SetDock(allowBtn, Dock.Left);
            confirmDock.Children.Add(allowBtn);

            var confirmLabel = new TextBlock
            {
                Text = Lang.S("ConfirmMsg"),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 50)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12
            };
            confirmDock.Children.Add(confirmLabel);

            _confirmPanel.Child = confirmDock;
            root.Children.Add(_confirmPanel);

            // Bottom separator
            var sep = new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) };
            sep.SetValue(DockPanel.DockProperty, Dock.Bottom);
            root.Children.Add(sep);

            // ---- Messages area ----
            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var itemsControl = new ItemsControl { ItemsSource = _messages };
            VirtualizingStackPanel.SetIsVirtualizing(itemsControl, true);
            ScrollViewer.SetCanContentScroll(itemsControl, true);
            itemsControl.ItemTemplate = CreateMessageTemplate();
            _scrollViewer.Content = itemsControl;
            root.Children.Add(_scrollViewer);

            Content = root;

            if (!_ai.HasApiKey)
                _messages.Add(new ChatMessage("System", Lang.S("NoApiKey")));
        }

        private DataTemplate CreateMessageTemplate()
        {
            var template = new DataTemplate(typeof(ChatMessage));

            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            factory.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));
            factory.SetValue(Border.MarginProperty, new Thickness(8, 3, 8, 3));
            factory.SetValue(Border.MaxWidthProperty, 600.0);

            var textFactory = new FrameworkElementFactory(typeof(SelectableTextBlock));
            textFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            textFactory.SetValue(TextBlock.FontSizeProperty, 12.0);
            textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));

            factory.AppendChild(textFactory);

            factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToBrushConverter() });
            factory.SetBinding(Border.HorizontalAlignmentProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToAlignmentConverter() });
            textFactory.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToForegroundConverter() });

            template.VisualTree = factory;
            return template;
        }

        // ---- Send Message ----

        private void OnSend(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                // Act as Stop button
                try { _cts?.Cancel(); } catch { }
                return;
            }

            var text = _inputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (text.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                _messages.Clear();
                _ai.StartNewSession();
                _inputBox.Clear();
                return;
            }

            _messages.Add(new ChatMessage("User", text));
            _inputBox.Clear();
            UpdateStatusInfo();

            _isProcessing = true;
            _sendBtn.Content = Lang.S("Stop");
            _sendBtn.Background = new SolidColorBrush(Color.FromRgb(160, 40, 40));
            _sendBtn.BorderBrush = Brushes.Transparent;
            _inputBox.IsEnabled = false;

            _cts = new System.Threading.CancellationTokenSource();
            var token = _cts.Token;
            var capturedText = text;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _ai.Chat(capturedText, token);
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isProcessing = false;
                        _sendBtn.Content = Lang.S("Send");
                        _sendBtn.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                        _sendBtn.BorderBrush = Brushes.Transparent;
                        _sendBtn.IsEnabled = true;
                        _inputBox.IsEnabled = true;
                        _inputBox.Focus();
                        UpdateStatusInfo();
                        if (_streamingMsg != null)
                        {
                            // Stream was interrupted or ended without OnAiTextEnd — flush
                            if (string.IsNullOrEmpty(_streamingMsg.Text))
                                _messages.Remove(_streamingMsg);
                            _streamingMsg = null;
                        }
                        try { _cts?.Dispose(); } catch { }
                        _cts = null;
                    }));
                }
            });
        }

        // ---- Auto-save Session ----

        private void AutoSaveSession()
        {
            if (_messages.Count == 0) return;
            var msgs = _messages.Select(m => new Dictionary<string, string>
            {
                { "role", m.Role },
                { "text", m.Text }
            }).ToList();
            _ai.SaveSession(_json.Serialize(msgs).Split('?').Select(x => x).ToArray()); // simplified
            // Actually save display messages
            try
            {
                _ai.SaveSession(null);
                // Re-save with actual messages
                var saveData = _messages.Select(m => new Dictionary<string, string>
                {
                    { "role", m.Role },
                    { "text", m.Text }
                }).ToList();
                var dataDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrioAI");
                var path = System.IO.Path.Combine(dataDir, "chat_history", _ai.CurrentSessionId + ".json");
                var data = new
                {
                    id = _ai.CurrentSessionId,
                    updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    messages = saveData
                };
                File.WriteAllText(path, _json.Serialize(data));
            }
            catch { }
        }

        // ---- About Dialog ----

        private void OnAbout(object sender, RoutedEventArgs e)
        {
            var win = new Window
            {
                Title = "关于 - TRIO AI助手",
                Width = 400,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40))
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = "免责声明",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "本插件由第三方开发（BY:LFMMD），与 TRIO Motion Technology 无关。\n\n插件按\"原样\"提供，不作任何明示或暗示的保证。使用本插件产生的任何风险由用户自行承担。",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var okBtn = new Button
            {
                Content = "确定",
                Width = 70,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            okBtn.Click += (s, ev) => win.Close();
            panel.Children.Add(okBtn);

            win.Content = panel;
            win.ShowDialog();
        }

        // ---- Settings Dialog ----

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var win = new Window
            {
                Title = Lang.S("SettingsTitle"),
                Width = 450,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40))
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            // API Key (masked)
            panel.Children.Add(MakeLabel(Lang.S("ApiKey")));
            var keyBox = new PasswordBox
            {
                Password = LoadConfigValue("apiKey"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0),
                PasswordChar = '*'
            };
            panel.Children.Add(keyBox);

            // API URL
            panel.Children.Add(MakeLabel(Lang.S("ApiUrl")));
            var urlBox = new TextBox
            {
                Text = LoadConfigValue("apiUrl"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0)
            };
            panel.Children.Add(urlBox);

            // Model
            panel.Children.Add(MakeLabel(Lang.S("Model")));
            var modelBox = new TextBox
            {
                Text = LoadConfigValue("model"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0)
            };
            panel.Children.Add(modelBox);

            // Show Tool Status checkbox
            var showStatusCheck = new CheckBox
            {
                Content = Lang.S("ShowToolStatus"),
                IsChecked = _ai.ShowToolStatus,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("ShowToolStatusDesc")
            };
            panel.Children.Add(showStatusCheck);

            // Include skill images checkbox
            var includeImagesCheck = new CheckBox
            {
                Content = Lang.S("IncludeSkillImages"),
                IsChecked = _ai.IncludeSkillImages,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("IncludeSkillImagesDesc")
            };
            panel.Children.Add(includeImagesCheck);

            // Controller validation checkbox
            var controllerValidationCheck = new CheckBox
            {
                Content = Lang.S("ControllerValidation"),
                IsChecked = AiService.EnableControllerValidation,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("ControllerValidationDesc")
            };
            panel.Children.Add(controllerValidationCheck);

            // Buttons
            var btnRow = new DockPanel { LastChildFill = false };

            var initBtn = new Button
            {
                Content = "初始化 Skill 数据",
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(80, 50, 0)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 80, 0))
            };
            var skillStatus = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Text = _ai.SkillsInitialized ? "已释放" : "",
                Foreground = _ai.SkillsInitialized
                    ? new SolidColorBrush(Color.FromRgb(0, 200, 0))
                    : Brushes.Gray
            };
            initBtn.Click += (s, ev) =>
            {
                if (_ai.SkillsInitialized)
                {
                    var r = MessageBox.Show(win, "Skill 数据已释放，是否覆盖？", "确认覆盖",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }
                var err = _ai.InitializeSkills();
                if (err != null)
                {
                    MessageBox.Show(win, err, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                MessageBox.Show(win, "Skill 数据初始化完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                skillStatus.Text = "已释放";
                skillStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 0));
            };
            DockPanel.SetDock(initBtn, Dock.Left);
            DockPanel.SetDock(skillStatus, Dock.Left);
            btnRow.Children.Add(initBtn);
            btnRow.Children.Add(skillStatus);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(btnPanel, Dock.Right);
            btnRow.Children.Add(btnPanel);

            var cancelBtn = new Button
            {
                Content = Lang.S("Cancel"),
                Width = 70, Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
            };
            cancelBtn.Click += (s, ev) => win.DialogResult = false;
            btnPanel.Children.Add(cancelBtn);

            var saveBtn = new Button
            {
                Content = Lang.S("Save"),
                Width = 70, Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            saveBtn.Click += (s, ev) =>
            {
                var key = keyBox.Password.Trim();
                var url = urlBox.Text.Trim();
                var model = modelBox.Text.Trim();
                if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(url))
                {
                    MessageBox.Show(win, Lang.S("EmptyKey"), Lang.S("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _ai.SaveConfig(key, model, url, showStatusCheck.IsChecked, includeImagesCheck.IsChecked, controllerValidationCheck.IsChecked);
                _messages.Add(new ChatMessage("System", Lang.S("SettingsSaved")));
                _scrollViewer.ScrollToEnd();
                win.DialogResult = true;
            };
            btnPanel.Children.Add(saveBtn);

            panel.Children.Add(btnRow);
            win.Content = panel;
            win.ShowDialog();
        }

        private void UpdateStatusInfo()
        {
            if (_statusInfo == null || _ai == null) return;
            var msgCount = _ai.HistoryMessageCount;
            var tokens = _ai.HistoryTokenEstimate;
            _statusInfo.Text = $"Msgs: {msgCount}  ~{tokens}K tokens";
        }

        private static TextBlock MakeLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private static string LoadConfigValue(string key)
        {
            try
            {
                var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrioAI", "config.json");
                if (!File.Exists(configPath))
                {
                    if (key == "apiUrl") return "https://api.deepseek.com/anthropic";
                    if (key == "model") return "claude-sonnet-4-20250514";
                    return "";
                }
                var text = File.ReadAllText(configPath);
                var cfg = _json.Deserialize<Dictionary<string, object>>(text);
                object val;
                if (cfg.TryGetValue(key, out val) && val != null && !string.IsNullOrEmpty(val.ToString()))
                    return val.ToString();
                // Return defaults for empty values
                if (key == "apiUrl") return "https://api.deepseek.com/anthropic";
                if (key == "model") return "claude-sonnet-4-20250514";
            }
            catch { }
            if (key == "apiUrl") return "https://api.deepseek.com/anthropic";
            if (key == "model") return "claude-sonnet-4-20250514";
            return "";
        }

        // ---- History Dialog ----

        private void OnHistory(object sender, RoutedEventArgs e)
        {
            var sessions = _ai.ListSessions();
            if (sessions.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this), Lang.S("NoHistory"), Lang.S("History"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new Window
            {
                Title = Lang.S("HistoryTitle"),
                Width = 420,
                Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40))
            };

            var dock = new DockPanel();

            // Buttons at bottom
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            btnPanel.SetValue(DockPanel.DockProperty, Dock.Bottom);

            var deleteBtn = new Button
            {
                Content = Lang.S("Delete"),
                Width = 70, Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(160, 40, 40)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            btnPanel.Children.Add(deleteBtn);

            var openBtn = new Button
            {
                Content = Lang.S("Open"),
                Width = 70, Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            btnPanel.Children.Add(openBtn);

            var closeBtn = new Button
            {
                Content = Lang.S("Close"),
                Width = 70, Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
            };
            closeBtn.Click += (s, ev) => win.Close();
            btnPanel.Children.Add(closeBtn);

            dock.Children.Add(btnPanel);

            // Separator
            var sep = new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) };
            sep.SetValue(DockPanel.DockProperty, Dock.Bottom);
            dock.Children.Add(sep);

            // List
            var listBox = new ListBox
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(4)
            };

            foreach (var session in sessions)
            {
                var item = new ListBoxItem
                {
                    Tag = session.Id,
                    Content = $"{session.Time}  {session.Preview}"
                };
                item.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                item.FontSize = 12;
                item.Padding = new Thickness(4, 6, 4, 6);
                item.BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                listBox.Items.Add(item);
            }

            openBtn.Click += (s, ev) =>
            {
                var selected = listBox.SelectedItem as ListBoxItem;
                if (selected == null)
                {
                    MessageBox.Show(win, Lang.S("SelectConversation"), Lang.S("History"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var sessionId = selected.Tag as string;
                LoadSessionMessages(sessionId);
                win.Close();
            };

            deleteBtn.Click += (s, ev) =>
            {
                var selected = listBox.SelectedItem as ListBoxItem;
                if (selected == null) return;
                var sessionId = selected.Tag as string;
                var confirm = MessageBox.Show(win, Lang.S("ConfirmDelete"), Lang.S("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Yes)
                {
                    _ai.DeleteSession(sessionId);
                    listBox.Items.Remove(selected);
                }
            };

            // Double-click to open
            listBox.MouseDoubleClick += (s, ev) =>
            {
                var selected = listBox.SelectedItem as ListBoxItem;
                if (selected == null) return;
                var sessionId = selected.Tag as string;
                LoadSessionMessages(sessionId);
                win.Close();
            };

            dock.Children.Add(listBox);
            win.Content = dock;
            win.ShowDialog();
        }

        private void LoadSessionMessages(string sessionId)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrioAI",
                    "chat_history", sessionId + ".json");
                if (!File.Exists(path)) return;

                var text = File.ReadAllText(path);
                var data = _json.Deserialize<Dictionary<string, object>>(text);
                _messages.Clear();
                _ai.StartNewSession();

                object msgsObj;
                if (data.TryGetValue("messages", out msgsObj) && msgsObj is System.Collections.ArrayList al)
                {
                    foreach (var m in al)
                    {
                        var md = m as Dictionary<string, object>;
                        if (md != null)
                        {
                            var role = md.ContainsKey("role") ? md["role"]?.ToString() : "System";
                            var msg = md.ContainsKey("text") ? md["text"]?.ToString() : "";
                            _messages.Add(new ChatMessage(role, msg));
                        }
                    }
                }
                _scrollViewer.ScrollToEnd();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Lang.S("Error")}: {ex.Message}", Lang.S("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---- Inline Confirmation ----

        private bool ShowInlineConfirmation(string toolName, string argsJson)
        {
            var tcs = new TaskCompletionSource<bool>();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _messages.Add(new ChatMessage("System", $"{Lang.S("AIRequests")}: {toolName}\n{Truncate(argsJson, 400)}"));
                _scrollViewer.ScrollToEnd();

                _inputPanel.Visibility = Visibility.Collapsed;
                _confirmPanel.Visibility = Visibility.Visible;
                _confirmTcs = tcs;
            }));

            return tcs.Task.Result;
        }

        private void OnConfirmAllow(object sender, RoutedEventArgs e)
        {
            _confirmPanel.Visibility = Visibility.Collapsed;
            _inputPanel.Visibility = Visibility.Visible;
            _confirmTcs?.SetResult(true);
            _confirmTcs = null;
            _inputBox.Focus();
        }

        private void OnConfirmReject(object sender, RoutedEventArgs e)
        {
            _confirmPanel.Visibility = Visibility.Collapsed;
            _inputPanel.Visibility = Visibility.Visible;
            _messages.Add(new ChatMessage("System", Lang.S("Rejected")));
            _scrollViewer.ScrollToEnd();
            _confirmTcs?.SetResult(false);
            _confirmTcs = null;
            _inputBox.Focus();
        }

        public void AddMessage(string role, string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _messages.Add(new ChatMessage(role, text));
                _scrollViewer.ScrollToEnd();
            }));
        }

        // --- Tool<TControl> inner class ---
        private class ChatTool : Tool<ChatPanel>
        {
            public ChatTool(ChatPanel control)
                : base(control, (ToolFactory)(object)_factory)
            {
                Title = Lang.S("Title");
            }
        }

        // --- ToolFactory<TControl> inner class ---
        private class ChatPanelFactory : ToolFactory<ChatPanel>
        {
            public readonly string ToolName;

            public ChatPanelFactory(ToolPositionSettings defaultPosition)
                : base(defaultPosition, (KnownFunctionalities)0)
            {
                ToolName = CreateToolName();
                RegisterFunctionality(
                    ToolName,
                    "AI Assistant",
                    "AI-powered MotionPerfect assistant",
                    null,
                    FunctionalityPath.Tools);
            }

            protected override void OnApplyConfiguration(string key)
            {
                var functionalitySettings = GetFunctionalitySettings(key);
                if (functionalitySettings != null)
                {
                    InstallUIComponents(functionalitySettings.Item1, (IFunctionalityAccessibilityCheck)(object)functionalitySettings.Item2);
                }
            }

            protected override FunctionalityItem CreateDescription(string functionalityKey)
            {
                var desc = new FunctionalityItem();
                var mi = CreateMenuItem();
                FunctionalityConfigurationExtensions.AddUIComponent(
                    desc,
                    "MainWindow.Menu.Tools.AIAssistant",
                    null,
                    mi,
                    "MainWindow.Menu.Tools",
                    null,
                    true);
                return desc;
            }

            private MenuItem CreateMenuItem()
            {
                var mi = new MenuItem { Header = Lang.S("Title") };
                mi.Tag = (ToolFactory)(object)this;
                mi.Click += OnMenuItemClick;
                return mi;
            }

            private static void OnMenuItemClick(object sender, RoutedEventArgs e)
            {
                AiService.PerfLog("OnMenuItemClick: enter");
                var mi = sender as MenuItem;
                if (mi?.Tag == null) return;
                var factory = mi.Tag as ToolFactory;
                if (factory == null) return;
                var mw = MPSingletons.MainWindow;
                if (mw == null) return;
                AiService.PerfLog("OnMenuItemClick: calling OpenToolWindow");
                mw.OpenToolWindow(factory.CreateToolName(), true);
                AiService.PerfLog("OnMenuItemClick: OpenToolWindow returned");
                AiService.PerfLogFlush();
                e.Handled = true;
            }

            private void InstallUIComponents(IFunctionalityConfiguration fConfiguration, IFunctionalityAccessibilityCheck settings)
            {
                FunctionalityToolFactoryExtensions.AddUICreator(
                    (ToolFactoryBase)(object)this,
                    "MainWindow.Menu.Tools",
                    "MainWindow.Menu.Tools.AIAssistant",
                    (Func<object>)(() => CreateMenuItem()),
                    fConfiguration,
                    settings);
            }
        }
    }

    internal class ChatMessage : System.ComponentModel.INotifyPropertyChanged
    {
        private string _text;
        public string Role { get; }
        public string Text
        {
            get { return _text; }
            set
            {
                if (_text != value)
                {
                    _text = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs("Text"));
                }
            }
        }
        public ChatMessage(string role, string text) { Role = role; _text = text; }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    #region Converters
    internal class RoleToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var role = value as string;
            if (role == "User") return new SolidColorBrush(Color.FromRgb(0, 100, 200));
            if (role == "System") return new SolidColorBrush(Color.FromRgb(60, 50, 20));
            return new SolidColorBrush(Color.FromRgb(55, 55, 55));
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    internal class RoleToAlignmentConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var role = value as string;
            if (role == "User") return HorizontalAlignment.Right;
            if (role == "System") return HorizontalAlignment.Center;
            return HorizontalAlignment.Left;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    internal class RoleToForegroundConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var role = value as string;
            if (role == "User") return Brushes.White;
            if (role == "System") return new SolidColorBrush(Color.FromRgb(255, 200, 50));
            return (Brush)new SolidColorBrush(Color.FromRgb(220, 220, 220));
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    #endregion

    internal class SelectableTextBlock : TextBlock
    {
        public SelectableTextBlock()
        {
            ContextMenu = new ContextMenu();
            var copyItem = new MenuItem { Header = Lang.S("Copy") };
            copyItem.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(Text))
                    Clipboard.SetText(Text);
            };
            ContextMenu.Items.Add(copyItem);

            var copyAllItem = new MenuItem { Header = Lang.S("CopyAll") };
            copyAllItem.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(Text))
                    Clipboard.SetText(Text);
            };
            ContextMenu.Items.Add(copyAllItem);
        }
    }

    internal static class Lang
    {
        private static readonly Dictionary<string, Dictionary<string, string>> _strings = new Dictionary<string, Dictionary<string, string>>
        {
            ["zh"] = new Dictionary<string, string>
            {
                { "Title", "TRIO AI助手" },
                { "New", "新对话" },
                { "Clear", "清空" },
                { "History", "历史" },
                { "Settings", "设置" },
                { "Send", "发送" },
                { "Stop", "停止" },
                { "Allow", "允许" },
                { "Reject", "拒绝" },
                { "ConfirmMsg", "允许 AI 执行此操作？" },
                { "Rejected", "用户拒绝了此操作。" },
                { "NoApiKey", "未配置 API Key。点击[设置]配置。" },
                { "SettingsSaved", "设置已保存。" },
                { "SettingsTitle", "AI 助手设置" },
                { "ApiKey", "API Key:" },
                { "ApiUrl", "API URL:" },
                { "Model", "模型:" },
                { "Save", "保存" },
                { "Cancel", "取消" },
                { "Error", "错误" },
                { "HistoryTitle", "对话历史" },
                { "NoHistory", "暂无历史记录。" },
                { "Open", "打开" },
                { "Delete", "删除" },
                { "Close", "关闭" },
                { "ConfirmDelete", "确认删除此对话？" },
                { "Confirm", "确认" },
                { "SelectConversation", "请选择一个对话。" },
                { "EmptyKey", "API Key 或 URL 不能为空。" },
                { "AIRequests", "AI 请求执行" },
                { "Copy", "复制" },
                { "CopyAll", "全部复制" },
                { "ShowToolStatus", "显示工具执行状态" },
                { "ShowToolStatusDesc", "在聊天中显示黄色工具调用状态信息" },
                { "IncludeSkillImages", "Skill 查询包含图片" },
                { "IncludeSkillImagesDesc", "查询 TrioBASIC/IEC/PLCOpen 指令时在返回内容中保留 <img> 标签（默认关闭以节省 Token）" },
                { "ControllerValidation", "控制器语法校验（仅模拟器）" },
                { "ControllerValidationDesc", "连接模拟器时，将代码逐行发送到控制器进行语法校验（ValidationService）。需要模拟器连接。" },
            },
            ["en"] = new Dictionary<string, string>
            {
                { "Title", "TRIO AI助手" },
                { "New", "New" },
                { "Clear", "Clear" },
                { "History", "History" },
                { "Settings", "Settings" },
                { "Send", "Send" },
                { "Stop", "Stop" },
                { "Allow", "Allow" },
                { "Reject", "Reject" },
                { "ConfirmMsg", "Allow AI to execute this operation?" },
                { "Rejected", "Operation rejected by user." },
                { "NoApiKey", "API key not configured. Click 'Settings' to set your API key." },
                { "SettingsSaved", "Settings saved." },
                { "SettingsTitle", "AI Assistant Settings" },
                { "ApiKey", "API Key:" },
                { "ApiUrl", "API URL:" },
                { "Model", "Model:" },
                { "Save", "Save" },
                { "Cancel", "Cancel" },
                { "Error", "Error" },
                { "HistoryTitle", "Conversation History" },
                { "NoHistory", "No history yet." },
                { "Open", "Open" },
                { "Delete", "Delete" },
                { "Close", "Close" },
                { "ConfirmDelete", "Delete this conversation?" },
                { "Confirm", "Confirm" },
                { "SelectConversation", "Please select a conversation." },
                { "EmptyKey", "API Key or URL cannot be empty." },
                { "AIRequests", "AI requests" },
                { "Copy", "Copy" },
                { "CopyAll", "Copy All" },
                { "ShowToolStatus", "Show Tool Status" },
                { "ShowToolStatusDesc", "Display yellow tool execution status messages in chat" },
                { "IncludeSkillImages", "Include images in skill lookups" },
                { "IncludeSkillImagesDesc", "Keep <img> tags when returning TrioBASIC/IEC/PLCOpen command help (off by default to save tokens)" },
                { "ControllerValidation", "Controller syntax validation (simulator only)" },
                { "ControllerValidationDesc", "When connected to a simulator, validate code line-by-line via EXECUTE parse. Requires simulator connection." },
            },
            ["de"] = new Dictionary<string, string>
            {
                { "Title", "TRIO AI助手" },
                { "New", "Neu" },
                { "Clear", "Leeren" },
                { "History", "Verlauf" },
                { "Settings", "Einstellungen" },
                { "Send", "Senden" },
                { "Stop", "Stopp" },
                { "Allow", "Zulassen" },
                { "Reject", "Ablehnen" },
                { "ConfirmMsg", "KI die Ausführung erlauben?" },
                { "Rejected", "Vom Benutzer abgelehnt." },
                { "NoApiKey", "API-Key nicht konfiguriert. Klicken Sie auf 'Einstellungen'." },
                { "SettingsSaved", "Einstellungen gespeichert." },
                { "SettingsTitle", "KI-Assistent Einstellungen" },
                { "ApiKey", "API Key:" },
                { "ApiUrl", "API URL:" },
                { "Model", "Modell:" },
                { "Save", "Speichern" },
                { "Cancel", "Abbrechen" },
                { "Error", "Fehler" },
                { "HistoryTitle", "Gesprächsverlauf" },
                { "NoHistory", "Kein Verlauf vorhanden." },
                { "Open", "Öffnen" },
                { "Delete", "Löschen" },
                { "Close", "Schließen" },
                { "ConfirmDelete", "Gespräch löschen?" },
                { "Confirm", "Bestätigen" },
                { "SelectConversation", "Bitte ein Gespräch auswählen." },
                { "EmptyKey", "API Key oder URL darf nicht leer sein." },
                { "AIRequests", "KI-Anfrage" },
                { "Copy", "Kopieren" },
                { "CopyAll", "Alles kopieren" },
                { "ShowToolStatus", "Tool-Status anzeigen" },
                { "ShowToolStatusDesc", "Gelbe Tool-Ausfuehrungsstatusmeldungen im Chat anzeigen" },
                { "IncludeSkillImages", "Bilder in Skill-Abfragen einschließen" },
                { "IncludeSkillImagesDesc", "<img>-Tags bei TrioBASIC/IEC/PLCOpen-Hilfeausgaben behalten (standardmäßig aus, um Tokens zu sparen)" },
                { "ControllerValidation", "Controller-Syntaxprüfung (nur Simulator)" },
                { "ControllerValidationDesc", "Bei Verbindung mit einem Simulator Code zeilenweise über EXECUTE parsen validieren." },
            },
            ["fr"] = new Dictionary<string, string>
            {
                { "Title", "TRIO AI助手" },
                { "New", "Nouveau" },
                { "Clear", "Effacer" },
                { "History", "Historique" },
                { "Settings", "Paramètres" },
                { "Send", "Envoyer" },
                { "Stop", "Arrêter" },
                { "Allow", "Autoriser" },
                { "Reject", "Refuser" },
                { "ConfirmMsg", "Autoriser l'IA à exécuter cette opération ?" },
                { "Rejected", "Opération refusée par l'utilisateur." },
                { "NoApiKey", "Clé API non configurée. Cliquez sur 'Paramètres'." },
                { "SettingsSaved", "Paramètres enregistrés." },
                { "SettingsTitle", "Paramètres de l'assistant IA" },
                { "ApiKey", "Clé API :" },
                { "ApiUrl", "URL API :" },
                { "Model", "Modèle :" },
                { "Save", "Enregistrer" },
                { "Cancel", "Annuler" },
                { "Error", "Erreur" },
                { "HistoryTitle", "Historique des conversations" },
                { "NoHistory", "Aucun historique." },
                { "Open", "Ouvrir" },
                { "Delete", "Supprimer" },
                { "Close", "Fermer" },
                { "ConfirmDelete", "Supprimer cette conversation ?" },
                { "Confirm", "Confirmer" },
                { "SelectConversation", "Veuillez sélectionner une conversation." },
                { "EmptyKey", "La clé API ou l'URL ne peut pas être vide." },
                { "AIRequests", "Requête IA" },
                { "Copy", "Copier" },
                { "CopyAll", "Tout copier" },
                { "ShowToolStatus", "Afficher statut outil" },
                { "ShowToolStatusDesc", "Afficher les messages d'etat d'execution des outils en jaune" },
                { "IncludeSkillImages", "Inclure les images dans les recherches de skill" },
                { "IncludeSkillImagesDesc", "Conserver les balises <img> lors du retour de l'aide TrioBASIC/IEC/PLCOpen (désactivé par défaut pour économiser des tokens)" },
                { "ControllerValidation", "Validation syntaxique du contrôleur (simulateur uniquement)" },
                { "ControllerValidationDesc", "Lorsque connecté au simulateur, valider le code ligne par ligne via EXECUTE parse." },
            },
        };

        private static string _langCode;

        public static string LangCode
        {
            get
            {
                if (_langCode == null)
                {
                    var culture = System.Threading.Thread.CurrentThread.CurrentUICulture.Name;
                    if (culture.StartsWith("zh")) _langCode = "zh";
                    else if (culture.StartsWith("de")) _langCode = "de";
                    else if (culture.StartsWith("fr")) _langCode = "fr";
                    else _langCode = "en";
                }
                return _langCode;
            }
        }

        public static string S(string key)
        {
            Dictionary<string, string> dict;
            if (_strings.TryGetValue(LangCode, out dict))
            {
                string val;
                if (dict.TryGetValue(key, out val)) return val;
            }
            // Fallback to English
            if (_strings["en"].TryGetValue(key, out var fallback)) return fallback;
            return key;
        }
    }
}
