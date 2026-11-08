using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    /// <summary>
    /// 极速灵感速记独立悬浮 HUD (借鉴并超越叶归一键记事)
    /// 0ms 纯原生桌面唤起、动态意图变色感应、30秒连续记录防碎块粘滞指示、Esc 0污染瞬退
    /// </summary>
    public class MiniInputWindow : Window
    {
        private TextBox _inputBox;
        private TextBlock _placeholder;
        private Border _rootBorder;
        private Border _intentPill;
        private TextBlock _intentIconText;
        private TextBlock _intentLabelText;
        private Border _statusBadge;
        private TextBlock _statusBadgeText;
        private StackPanel _suggestionPanel;
        private StackPanel _lifelogChipsPanel;
        private readonly System.Collections.Generic.List<string> _suggestions = new System.Collections.Generic.List<string>();
        private int _activeSuggestionIndex = 0;
        private static string _savedDraft = "";
        private static readonly System.Collections.Generic.List<string> _recentInputHistory = new System.Collections.Generic.List<string>();
        private int _historyIndex = -1;
        private bool _isStickyMode = false;
        private string _stickyBlockId = "";

        public event Action<string> OnTextSubmitted;

        public MiniInputWindow() : this(null) { }

        public MiniInputWindow(ContextSnapshot snapshot)
        {
            Title = "思源外骨骼 · 极速灵感与生活流速记 (Lifelog HUD)";
            Width = 660;
            Height = 88;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowPositionHelper.CenterOnCursorScreen(this, -40);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;

            // 原生拖拽直投支持 (支持拖拽图片/音频/本地文件直接附带)
            AllowDrop = true;
            DragEnter += (s_ev, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            };
            Drop += (s_ev, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        foreach (var f in files)
                        {
                            string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp")
                            {
                                string mdImg = "![" + System.IO.Path.GetFileName(f) + "](" + f + ") ";
                                int c = _inputBox.CaretIndex;
                                _inputBox.Text = _inputBox.Text.Insert(c, mdImg);
                                _inputBox.CaretIndex = c + mdImg.Length;
                                _statusBadgeText.Text = "🖼️ 已附加拖拽图片";
                            }
                            else if (ext == ".mp3" || ext == ".wav" || ext == ".m4a")
                            {
                                _inputBox.Text = "🎙️ 录音: " + System.IO.Path.GetFileName(f) + "\n" + f;
                                _statusBadgeText.Text = "🎙️ 已挂载录音文件";
                            }
                            else
                            {
                                string mdLink = "[" + System.IO.Path.GetFileName(f) + "](" + f + ") ";
                                int c = _inputBox.CaretIndex;
                                _inputBox.Text = _inputBox.Text.Insert(c, mdLink);
                                _inputBox.CaretIndex = c + mdLink.Length;
                                _statusBadgeText.Text = "📎 已附加文件链接";
                            }
                        }
                    }
                }
            };

            _rootBorder = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f172a")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f46e5")),
                BorderThickness = new Thickness(1.5),
                ClipToBounds = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 32,
                    ShadowDepth = 8,
                    Opacity = 0.70
                }
            };

            var grid = new Grid
            {
                Margin = new Thickness(10, 0, 12, 0)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 1. 左侧动态意图胶囊徽标 (根据输入前缀变色感应)
            _intentPill = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e1b4b")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366f1")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var intentStack = new StackPanel { Orientation = Orientation.Horizontal };
            _intentIconText = new TextBlock
            {
                Text = "⚡",
                FontSize = 13,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _intentLabelText = new TextBlock
            {
                Text = "闪念",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#a5b4fc")),
                VerticalAlignment = VerticalAlignment.Center
            };
            intentStack.Children.Add(_intentIconText);
            intentStack.Children.Add(_intentLabelText);
            _intentPill.Child = intentStack;
            _intentPill.MouseDown += (s, e) =>
            {
                if (_isStickyMode && e.ChangedButton == MouseButton.Left)
                {
                    Topmost = !Topmost;
                    _statusBadgeText.Text = Topmost ? "📌 置顶 (Ctrl+S同步)" : "📎 取消置顶 (Ctrl+S同步)";
                }
            };
            Grid.SetColumn(_intentPill, 0);
            grid.Children.Add(_intentPill);

            // 2. 占位提示与输入框容器
            var inputGrid = new Grid();
            Grid.SetColumn(inputGrid, 1);

            string placeholderText = "随手一记... 支持 ¥账目、~专注、mood:心情、+打卡、!待办";
            if (snapshot != null)
            {
                if (snapshot.HasUrl && !string.IsNullOrEmpty(snapshot.CleanTitle))
                {
                    string shortTitle = snapshot.CleanTitle.Length > 26 ? snapshot.CleanTitle.Substring(0, 26) + "..." : snapshot.CleanTitle;
                    placeholderText = "来自 " + snapshot.AppDisplayName + " · " + shortTitle;
                }
                else if (!string.IsNullOrEmpty(snapshot.AppDisplayName) && snapshot.AppDisplayName != "桌面应用")
                {
                    placeholderText = "来自 " + snapshot.AppDisplayName + " · 随手记一笔...";
                }
            }

            _placeholder = new TextBlock
            {
                Text = placeholderText,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748b")),
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            inputGrid.Children.Add(_placeholder);

            _inputBox = new TextBox
            {
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f8fafc")),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                CaretBrush = Brushes.White
            };
            _inputBox.TextChanged += (s, e) =>
            {
                UpdateIntentDisplay(_inputBox.Text);
                UpdateSuggestions(_inputBox.Text);
            };
            inputGrid.Children.Add(_inputBox);
            grid.Children.Add(inputGrid);

            // 3. 右侧状态指示与高频动作按钮 (时间戳盖章 / 待办转换 / 粘滞续写 / 回车确认)
            string badgeText = "↵ 入库";
            if (snapshot != null && snapshot.IsDevWorkspace && !string.IsNullOrEmpty(snapshot.ProjectName))
            {
                badgeText = "🚀 " + (snapshot.ProjectName.Length > 12 ? snapshot.ProjectName.Substring(0, 12) + "..." : snapshot.ProjectName);
            }
            else if (snapshot != null && !string.IsNullOrEmpty(snapshot.AppDisplayName) && snapshot.AppDisplayName != "桌面应用")
            {
                badgeText = "↵ " + snapshot.AppDisplayName;
            }

            _statusBadge = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "点击或回车立即提交入库"
            };
            _statusBadgeText = new TextBlock
            {
                Text = badgeText,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94a3b8")),
                FontSize = 11,
                FontWeight = FontWeights.Bold
            };
            _statusBadge.Child = _statusBadgeText;
            _statusBadge.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    if (_isStickyMode)
                    {
                        SaveStickyContent();
                    }
                    else
                    {
                        SubmitCurrentText();
                    }
                }
            };

            var headerActionStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            // 快捷一键盖当前时间戳按钮 (🕒 14:35)
            var timeBtn = CreateHeaderActionButton("🕒 " + DateTime.Now.ToString("HH:mm"), "点击一键在输入框前置盖上当前时间戳 (快捷键: Ctrl+T)", () =>
            {
                StampCurrentTime();
            });

            // 快捷待办事项转换按钮 (📋 待办)
            var todoBtn = CreateHeaderActionButton("📋 待办", "点击一键转换为待办清单事项 (快捷键: Ctrl+D)", () =>
            {
                ToggleTodo();
            });

            // 快捷语音速记转换按钮 (🎙️ 语音)
            var voiceBtn = CreateHeaderActionButton("🎙️ 语音", "点击立即启动桌面语音速记 (支持微信输入法语音/讯飞/Win+H，快捷键: Ctrl+M)", () =>
            {
                TriggerVoiceMode();
            });

            // 快捷 AI 前置提炼按钮 (🤖 提炼)
            var aiBtn = CreateHeaderActionButton("🤖 提炼", "调用本地 Ollama 或国内大模型提炼核心3要点 (快捷键: Ctrl+J)", () =>
            {
                TriggerAiRefine("takeaways");
            });

            // 快捷填入并清洗剪贴板文本按钮 (📋 贴板)
            var clipBtn = CreateHeaderActionButton("📋 贴板", "快速填入并清洗剪贴板文本 (快捷键: Ctrl+Shift+V)", () =>
            {
                PasteAndCleanClipboard();
            });

            headerActionStack.Children.Add(timeBtn);
            headerActionStack.Children.Add(clipBtn);
            headerActionStack.Children.Add(todoBtn);
            headerActionStack.Children.Add(voiceBtn);
            headerActionStack.Children.Add(aiBtn);
            headerActionStack.Children.Add(_statusBadge);

            Grid.SetColumn(headerActionStack, 2);
            grid.Children.Add(headerActionStack);

            grid.Height = 48;
            _suggestionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(14, 0, 14, 6),
                Visibility = Visibility.Collapsed
            };

            // Lifelog 微时刻语法引导磁贴栏 (一键输入前置语法糖或快捷打卡)
            _lifelogChipsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10, 0, 10, 6)
            };
            _lifelogChipsPanel.Children.Add(CreateChip("🕒", "时间", () => StampCurrentTime()));
            _lifelogChipsPanel.Children.Add(CreateChip("📋", "待办", () => ToggleTodo()));
            _lifelogChipsPanel.Children.Add(CreateChip("🎙️", "语音", () => TriggerVoiceMode()));
            _lifelogChipsPanel.Children.Add(CreateChip("💰", "记账", () => SmartPrependOrFill("¥")));
            _lifelogChipsPanel.Children.Add(CreateChip("⏱️", "专注", () => SmartPrependOrFill("~25m ")));
            _lifelogChipsPanel.Children.Add(CreateChip("💧", "喝水", () => SmartPrependOrFill("+water 300ml ")));
            _lifelogChipsPanel.Children.Add(CreateChip("☕", "微憩", () => SmartPrependOrFill("~10m 休息微憩 ")));
            _lifelogChipsPanel.Children.Add(CreateChip("🏃", "运动", () => SmartPrependOrFill("+run 5km ")));
            _lifelogChipsPanel.Children.Add(CreateChip("🥗", "饮食", () => SmartPrependOrFill("eat: ")));
            _lifelogChipsPanel.Children.Add(CreateChip("🎭", "心情", () => SmartPrependOrFill("mood: 😄 ")));
            _lifelogChipsPanel.Children.Add(CreateChip("💡", "闪卡", () => SmartPrependOrFill("? ")));
            _lifelogChipsPanel.Children.Add(CreateChip("👥", "人脉", () => SmartPrependOrFill("@")));
            _lifelogChipsPanel.Children.Add(CreateChip(">>", "换轨", () => SmartPrependOrFill(">>")));

            if (snapshot != null && snapshot.IsDevWorkspace && !string.IsNullOrEmpty(snapshot.ProjectName))
            {
                string pName = snapshot.ProjectName;
                _lifelogChipsPanel.Children.Insert(0, CreateChip("🚀", "#" + pName, () => SmartPrependOrFill("#" + pName + " ")));
            }

            var mainStack = new StackPanel();
            mainStack.Children.Add(grid);
            mainStack.Children.Add(_suggestionPanel);
            mainStack.Children.Add(_lifelogChipsPanel);

            _rootBorder.Child = mainStack;
            Content = _rootBorder;

            // 检查防碎块粘滞窗口
            int remainingSec;
            if (SmartCaptureMain.IsWithinContinuationWindow(out remainingSec))
            {
                _statusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#064e3b"));
                _statusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#059669"));
                _statusBadgeText.Text = "⏳ 续写合并 (" + remainingSec + "s)";
                _statusBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6ee7b7"));
            }

            Action saveDraftAction = () =>
            {
                string curr = _inputBox.Text.Trim();
                _savedDraft = curr;
            };

            MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    try { DragMove(); } catch { }
                }
            };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (!_isStickyMode && !string.IsNullOrEmpty(_inputBox.Text))
                    {
                        _inputBox.Text = "";
                        _statusBadgeText.Text = "↵ 入库";
                        e.Handled = true;
                        return;
                    }
                    saveDraftAction();
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (Clipboard.ContainsImage())
                    {
                        try
                        {
                            var img = Clipboard.GetImage();
                            if (img != null)
                            {
                                string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XiaoLvDesktop");
                                if (!System.IO.Directory.Exists(tmpDir)) System.IO.Directory.CreateDirectory(tmpDir);
                                string tmpFile = System.IO.Path.Combine(tmpDir, "clip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                                using (var fs = new System.IO.FileStream(tmpFile, System.IO.FileMode.Create))
                                {
                                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                                    enc.Save(fs);
                                }
                                string mdImg = "![" + System.IO.Path.GetFileName(tmpFile) + "](" + tmpFile + ") ";
                                int c = _inputBox.CaretIndex;
                                _inputBox.Text = _inputBox.Text.Insert(c, mdImg);
                                _inputBox.CaretIndex = c + mdImg.Length;
                                _statusBadgeText.Text = "🖼️ 已附加截图";
                                e.Handled = true;
                                return;
                            }
                        }
                        catch { }
                    }
                }
                else if (e.Key == Key.T && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    StampCurrentTime();
                    e.Handled = true;
                }
                else if (e.Key == Key.D && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    ToggleTodo();
                    e.Handled = true;
                }
                else if (e.Key == Key.D1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    ToggleTodo();
                    e.Handled = true;
                }
                else if (e.Key == Key.D2 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    SmartPrependOrFill("¥");
                    e.Handled = true;
                }
                else if (e.Key == Key.D3 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    SmartPrependOrFill("~25m ");
                    e.Handled = true;
                }
                else if (e.Key == Key.D4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    SmartPrependOrFill("+water 300ml ");
                    e.Handled = true;
                }
                else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (_isStickyMode)
                    {
                        SaveStickyContent();
                    }
                    else
                    {
                        SubmitCurrentText();
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.J && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    TriggerAiRefine("takeaways");
                    e.Handled = true;
                }
                else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    PasteAndCleanClipboard();
                    e.Handled = true;
                }
                else if (e.Key == Key.Up && _suggestions.Count == 0 && (!_isScratchpadMode || _inputBox.CaretIndex == 0))
                {
                    lock (_recentInputHistory)
                    {
                        if (_recentInputHistory.Count > 0)
                        {
                            if (_historyIndex + 1 < _recentInputHistory.Count)
                            {
                                _historyIndex++;
                                _inputBox.Text = _recentInputHistory[_historyIndex];
                                _inputBox.CaretIndex = _inputBox.Text.Length;
                                _statusBadgeText.Text = "📜 历史 (" + (_historyIndex + 1) + "/" + _recentInputHistory.Count + ")";
                            }
                            e.Handled = true;
                        }
                    }
                }
                else if (e.Key == Key.Down && _suggestions.Count == 0 && (!_isScratchpadMode || _inputBox.CaretIndex == _inputBox.Text.Length))
                {
                    lock (_recentInputHistory)
                    {
                        if (_historyIndex > 0)
                        {
                            _historyIndex--;
                            _inputBox.Text = _recentInputHistory[_historyIndex];
                            _inputBox.CaretIndex = _inputBox.Text.Length;
                            _statusBadgeText.Text = "📜 历史 (" + (_historyIndex + 1) + "/" + _recentInputHistory.Count + ")";
                            e.Handled = true;
                        }
                        else if (_historyIndex == 0)
                        {
                            _historyIndex = -1;
                            _inputBox.Text = "";
                            _statusBadgeText.Text = "↵ 入库";
                            e.Handled = true;
                        }
                    }
                }
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && (e.Key == Key.OemPlus || e.Key == Key.Add))
                {
                    if (_inputBox.FontSize < 28) _inputBox.FontSize += 1.5;
                    _statusBadgeText.Text = string.Format("🔍 字号: {0:0.#}pt", _inputBox.FontSize);
                    e.Handled = true;
                }
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
                {
                    if (_inputBox.FontSize > 10) _inputBox.FontSize -= 1.5;
                    _statusBadgeText.Text = string.Format("🔍 字号: {0:0.#}pt", _inputBox.FontSize);
                    e.Handled = true;
                }
                else if (e.Key == Key.Tab && _suggestions.Count > 0)
                {
                    string chosen = (_activeSuggestionIndex >= 0 && _activeSuggestionIndex < _suggestions.Count)
                        ? _suggestions[_activeSuggestionIndex]
                        : _suggestions[0];
                    ApplySuggestion(chosen);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && _inputBox.CaretIndex == _inputBox.Text.Length && _suggestions.Count > 0)
                {
                    _activeSuggestionIndex = (_activeSuggestionIndex + 1) % _suggestions.Count;
                    RenderSuggestions();
                    e.Handled = true;
                }
                else if (e.Key == Key.Left && _inputBox.CaretIndex == 0 && _suggestions.Count > 0)
                {
                    _activeSuggestionIndex = (_activeSuggestionIndex - 1 + _suggestions.Count) % _suggestions.Count;
                    RenderSuggestions();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        ExpandToScratchpadMode();
                        int c = _inputBox.CaretIndex;
                        _inputBox.Text = _inputBox.Text.Insert(c, Environment.NewLine);
                        _inputBox.CaretIndex = c + Environment.NewLine.Length;
                        e.Handled = true;
                        return;
                    }

                    SubmitCurrentText();
                    e.Handled = true;
                }
            };

            Deactivated += (s, e) =>
            {
                saveDraftAction();
                Close();
            };

            // 窗口关闭即时触发物理内存深度压缩
            Closed += (s, e) => MemoryOptimizer.ScheduleTrim(300);

            Loaded += (s, e) =>
            {
                _inputBox.Focus();
                // 自动恢复未完成的草稿，防误关丢灵感
                if (!string.IsNullOrEmpty(_savedDraft))
                {
                    if (_savedDraft.Contains("\n"))
                    {
                        ExpandToScratchpadMode();
                    }
                    _inputBox.Text = _savedDraft;
                    _inputBox.SelectAll();
                    _statusBadgeText.Text = "📝 草稿已恢复";
                    _statusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#312e81"));
                    _statusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366f1"));
                }
            };
        }

        private bool _isScratchpadMode = false;

        private void ExpandToScratchpadMode()
        {
            if (_isScratchpadMode) return;
            _isScratchpadMode = true;
            Height = 175;
            _inputBox.AcceptsReturn = true;
            _inputBox.TextWrapping = TextWrapping.Wrap;
            _inputBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _inputBox.Height = 85;
            _inputBox.VerticalAlignment = VerticalAlignment.Stretch;
            _statusBadgeText.Text = "↵ 批量解包入库";
            _statusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#064e3b"));
            _statusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10b981"));
            if (_lifelogChipsPanel != null) _lifelogChipsPanel.Visibility = Visibility.Collapsed;

            // 智能防溢出：若展开后底部超出屏幕工作区，自动向上抬升
            try
            {
                var curPos = SelectionHelper.GetCurrentCursorPos();
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)curPos.X, (int)curPos.Y));
                double scale = WindowPositionHelper.GetDpiScaleForPoint((int)curPos.X, (int)curPos.Y);
                double bottomDip = (screen.WorkingArea.Top + screen.WorkingArea.Height) / scale;
                if (Top + Height > bottomDip - 10)
                {
                    Top = bottomDip - Height - 10;
                }
            }
            catch { }
        }

        private void UpdateIntentDisplay(string raw)
        {
            _placeholder.Visibility = string.IsNullOrEmpty(raw) ? Visibility.Visible : Visibility.Collapsed;
            var t = (raw ?? "").Trim();

            if (_isScratchpadMode || raw.Contains("\n"))
            {
                int lineCount = raw.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                SetIntentTheme("📝", "便签 (" + Math.Max(1, lineCount) + "行)", "#1e1b4b", "#6366f1", "#a5b4fc");
                _statusBadgeText.Text = "↵ 解包 " + Math.Max(1, lineCount) + " 行 (Ctrl+S即发)";
                return;
            }

            var entry = LifelogParser.Parse(raw);
            SetIntentTheme(entry.DisplayIcon, entry.CategoryLabel, entry.ThemePillBg, entry.ThemeBorderHex, entry.ThemeTextHex);

            if (entry.Category != LifelogCategory.Note)
            {
                _statusBadgeText.Text = "↵ 入 " + entry.SuggestedHeading;
            }
            else
            {
                _statusBadgeText.Text = "↵ 入库";
            }
        }

        private void SubmitCurrentText()
        {
            string txt = _inputBox.Text.Trim();
            _savedDraft = "";
            Close();
            if (!string.IsNullOrEmpty(txt))
            {
                lock (_recentInputHistory)
                {
                    _recentInputHistory.Remove(txt);
                    _recentInputHistory.Insert(0, txt);
                    if (_recentInputHistory.Count > 30) _recentInputHistory.RemoveAt(30);
                }

                if (OnTextSubmitted != null)
                {
                    // 1. 简码与宏占位符自动展开 (如 :date, :time, :now, :meeting, :thx)
                    if (txt.StartsWith(":") || txt.StartsWith("：") || txt.Contains("{{"))
                    {
                        txt = SnippetExpansionEngine.Expand(txt);
                    }

                    // 2. 智能口语去噪与自愈检测：若包含口头语或口述标点指令，自动完成去噪净化
                    if (txt.Contains("逗号") || txt.Contains("句号") || txt.Contains("问号") ||
                        txt.Contains("感叹号") || txt.Contains("换行") || txt.Contains("呃") ||
                        txt.Contains("那个就是说") || txt.Contains("怎么说呢"))
                    {
                        txt = VoiceCaptureManager.CleanVoiceSpeech(txt);
                    }
                    OnTextSubmitted(txt);
                }
            }
        }

        private void PasteAndCleanClipboard()
        {
            try
            {
                string clip = NativeClipboard.GetTextSafe();
                if (!string.IsNullOrEmpty(clip))
                {
                    string cleanedClip = TextCleaner.CleanAll(clip);
                    if (!string.IsNullOrEmpty(_inputBox.SelectedText))
                    {
                        _inputBox.SelectedText = cleanedClip;
                    }
                    else
                    {
                        int c = _inputBox.CaretIndex;
                        _inputBox.Text = _inputBox.Text.Insert(c, cleanedClip);
                        _inputBox.CaretIndex = c + cleanedClip.Length;
                    }
                    SoundPlayerHelper.PlayFeedback();
                    _statusBadgeText.Text = "📋 已填入剪贴板";
                }
            }
            catch { }
        }

        private void StampCurrentTime(bool includeSeconds = false)
        {
            string time = includeSeconds ? DateTime.Now.ToString("HH:mm:ss") : DateTime.Now.ToString("HH:mm");
            string stamp = time + " ";
            string cur = _inputBox.Text ?? "";

            if (string.IsNullOrEmpty(cur))
            {
                _inputBox.Text = stamp;
                _inputBox.CaretIndex = stamp.Length;
            }
            else
            {
                if (!Regex.IsMatch(cur, @"^\d{1,2}:\d{2}"))
                {
                    _inputBox.Text = stamp + cur;
                    _inputBox.CaretIndex = _inputBox.Text.Length;
                }
            }
            _inputBox.Focus();
        }

        private void ToggleTodo()
        {
            string cur = (_inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(cur))
            {
                _inputBox.Text = "! ";
                _inputBox.CaretIndex = 2;
            }
            else if ((cur.StartsWith("!") || cur.StartsWith("！")) && !cur.StartsWith("![") && !cur.StartsWith("！["))
            {
                _inputBox.Text = cur.TrimStart('!', '！', ' ');
                _inputBox.CaretIndex = _inputBox.Text.Length;
            }
            else if (cur.StartsWith("- [ ]"))
            {
                _inputBox.Text = cur.Substring(5).Trim();
                _inputBox.CaretIndex = _inputBox.Text.Length;
            }
            else
            {
                _inputBox.Text = "! " + cur;
                _inputBox.CaretIndex = _inputBox.Text.Length;
            }
            _inputBox.Focus();
        }

        private void SmartPrependOrFill(string prefix, string defaultContentIfEmpty = "")
        {
            string cur = (_inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(cur))
            {
                string toFill = !string.IsNullOrEmpty(defaultContentIfEmpty) ? (prefix + defaultContentIfEmpty) : prefix;
                _inputBox.Text = toFill;
                _inputBox.CaretIndex = _inputBox.Text.Length;
            }
            else
            {
                if (!cur.StartsWith(prefix.Trim()))
                {
                    _inputBox.Text = prefix + cur;
                    _inputBox.CaretIndex = _inputBox.Text.Length;
                }
            }
            _inputBox.Focus();
        }

        private void TriggerVoiceMode()
        {
            _statusBadgeText.Text = "🎙️ 正在聆听...";
            _statusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#082f49"));
            _statusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284c7"));
            _inputBox.Focus();
            VoiceCaptureManager.TriggerWindowsVoiceTyping();
        }

        private void TriggerAiRefine(string actionType)
        {
            string curTxt = _inputBox.Text.Trim();
            if (string.IsNullOrEmpty(curTxt))
            {
                _statusBadgeText.Text = "⚠️ 请先输入待提炼内容";
                return;
            }

            string prompt = AiRefiner.PromptKeyPoints;
            string actionLabel = "提炼要点";
            if (actionType == "todo") { prompt = AiRefiner.PromptActionItems; actionLabel = "提取待办"; }
            else if (actionType == "polish") { prompt = AiRefiner.PromptPolish; actionLabel = "规范排版"; }
            else if (actionType == "translate") { prompt = AiRefiner.PromptTranslate; actionLabel = "学术翻译"; }

            _statusBadgeText.Text = "⏳ AI " + actionLabel + "中...";
            ThreadPool.QueueUserWorkItem(st =>
            {
                var res = AiRefiner.Refine(prompt, curTxt);
                Dispatcher.Invoke(() =>
                {
                    if (res != null && res.Success && !string.IsNullOrEmpty(res.Content))
                    {
                        SoundPlayerHelper.PlayFeedback();
                        _inputBox.Text = res.Content;
                        _inputBox.CaretIndex = _inputBox.Text.Length;
                        _statusBadgeText.Text = "✓ AI " + actionLabel + "完成 (" + res.LatencyMs + "ms)";
                    }
                    else
                    {
                        _statusBadgeText.Text = "⚠️ AI 响应异常: " + (res != null ? res.ErrorMessage : "超时");
                    }
                });
            });
        }

        private Border CreateHeaderActionButton(string label, string tooltip, Action onClick)
        {
            var b = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            var tb = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cbd5e1")),
                FontSize = 11,
                FontWeight = FontWeights.Bold
            };
            b.Child = tb;
            b.MouseEnter += (s, e) =>
            {
                b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                b.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748b"));
                tb.Foreground = Brushes.White;
            };
            b.MouseLeave += (s, e) =>
            {
                b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b"));
                b.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                tb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cbd5e1"));
            };
            b.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    onClick();
                    _inputBox.Focus();
                }
            };
            return b;
        }

        private Border CreateChip(string icon, string label, Action onClick)
        {
            var b = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 5, 0),
                Cursor = Cursors.Hand
            };
            var st = new StackPanel { Orientation = Orientation.Horizontal };
            st.Children.Add(new TextBlock { Text = icon, FontSize = 10, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
            st.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94a3b8")), VerticalAlignment = VerticalAlignment.Center });
            b.Child = st;

            b.MouseEnter += (s, e) => { b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")); };
            b.MouseLeave += (s, e) => { b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b")); };
            b.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    onClick();
                    _inputBox.Focus();
                }
            };
            return b;
        }

        private void SetIntentTheme(string icon, string label, string pillBg, string borderHex, string textHex)
        {
            _intentIconText.Text = icon;
            _intentLabelText.Text = label;
            _intentPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pillBg));
            _intentPill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderHex));
            _intentLabelText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textHex));
            _rootBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderHex));
        }

        private void UpdateSuggestions(string raw)
        {
            var t = (raw ?? "").TrimStart();
            _suggestions.Clear();
            _activeSuggestionIndex = 0;

            if (t.StartsWith(">>") || t.StartsWith("》》"))
            {
                _suggestions.Add(">>📥 临时收集箱");
                _suggestions.Add(">>💡 闪念灵感");
                _suggestions.Add(">>📋 今日待办");
                _suggestions.Add(">>🎯 重点任务");
            }
            else if (t.StartsWith("@") || t.StartsWith("＠"))
            {
                _suggestions.Add("@张三");
                _suggestions.Add("@李四");
                _suggestions.Add("@项目负责人");
            }
            else if (t.StartsWith("+") || t.StartsWith("＋"))
            {
                _suggestions.Add("+water 500ml");
                _suggestions.Add("+run 5km");
                _suggestions.Add("+read 30min");
                _suggestions.Add("+weight 65kg");
            }
            else if (t.StartsWith("~") || t.StartsWith("～"))
            {
                _suggestions.Add("~25m 深度专注");
                _suggestions.Add("~45m 架构设计");
                _suggestions.Add("~1h 会议沟通");
            }
            else if (t.StartsWith("mood:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("mood：", StringComparison.OrdinalIgnoreCase) || t.StartsWith("情绪:") || t.StartsWith("情绪："))
            {
                _suggestions.Add("mood: 😄 精神饱满");
                _suggestions.Add("mood: 🔥 灵感爆发");
                _suggestions.Add("mood: ☕ 午后微憩");
                _suggestions.Add("mood: 😴 略有疲惫");
            }
            else if (t.StartsWith("eat:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("eat：", StringComparison.OrdinalIgnoreCase) || t.StartsWith("饮食:") || t.StartsWith("饮食："))
            {
                _suggestions.Add("eat: 燕麦酸奶早餐");
                _suggestions.Add("eat: 低脂鸡胸肉沙拉");
                _suggestions.Add("eat: 咖啡配全麦面包");
            }
            else if (t.StartsWith("av:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("av：", StringComparison.OrdinalIgnoreCase))
            {
                _suggestions.Add("av:读库清单");
                _suggestions.Add("av:项目任务");
                _suggestions.Add("av:日常收支");
            }
            else if (t.StartsWith("#") || t.StartsWith("＃"))
            {
                _suggestions.Add("#工作");
                _suggestions.Add("#学习");
                _suggestions.Add("#复盘");
            }
            else if (t.StartsWith(":") || t.StartsWith("："))
            {
                string p = t.StartsWith("：") ? (":" + t.Substring(1)) : t;
                var matched = SnippetExpansionEngine.GetMatchingSnippets(p);
                foreach (var sn in matched)
                {
                    _suggestions.Add(sn.Shortcode + " " + sn.Label);
                }
            }
            else
            {
                // 双向链接智能补全 ([[ 或 【【 实时反查思源知识库)
                int bIdx = Math.Max(t.LastIndexOf("[["), t.LastIndexOf("【【"));
                if (bIdx >= 0)
                {
                    string kw = t.Substring(bIdx + 2).Trim();
                    if (kw.Length >= 1)
                    {
                        string safeKw = kw.Replace("'", "''");
                        ThreadPool.QueueUserWorkItem(async s =>
                        {
                            try
                            {
                                string sql = "SELECT id, content FROM blocks WHERE (content LIKE '%" + safeKw + "%' OR name LIKE '%" + safeKw + "%' OR alias LIKE '%" + safeKw + "%') AND type IN ('d', 'h', 'p') " +
                                             "ORDER BY (CASE " +
                                             "  WHEN content = '" + safeKw + "' OR name = '" + safeKw + "' THEN 100 " +
                                             "  WHEN content LIKE '" + safeKw + "%' OR name LIKE '" + safeKw + "%' THEN 70 " +
                                             "  WHEN type = 'd' THEN 50 " +
                                             "  WHEN type = 'h' THEN 30 " +
                                             "  ELSE 10 " +
                                             "END) DESC, updated DESC LIMIT 5";
                                var rows = await SiYuanClient.SqlAsync(sql);
                                if (rows != null && rows.Count > 0)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        _suggestions.Clear();
                                        foreach (var r in rows)
                                        {
                                            string bid = JsonHelper.GetString(r, "id");
                                            string bcontent = JsonHelper.GetString(r, "content");
                                            string title = (!string.IsNullOrEmpty(bcontent) ? bcontent.Replace("\r", " ").Replace("\n", " ").Trim() : "无标题");
                                            if (title.Length > 20) title = title.Substring(0, 20) + "...";
                                            _suggestions.Add("((" + bid + " \"" + title + "\"))");
                                        }
                                        RenderSuggestions();
                                    });
                                }
                            }
                            catch { }
                        });
                    }
                }
            }

            RenderSuggestions();
        }

        private void RenderSuggestions()
        {
            _suggestionPanel.Children.Clear();
            if (_suggestions.Count == 0)
            {
                _suggestionPanel.Visibility = Visibility.Collapsed;
                if (!_isScratchpadMode) Height = 84;
                return;
            }

            _suggestionPanel.Visibility = Visibility.Visible;
            if (!_isScratchpadMode) Height = 114;

            for (int i = 0; i < _suggestions.Count; i++)
            {
                int idx = i;
                string sug = _suggestions[i];
                bool isActive = (idx == _activeSuggestionIndex);

                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#312e81" : "#1e293b")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#6366f1" : "#334155")),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand
                };

                var tb = new TextBlock
                {
                    Text = sug + (isActive ? " (Tab)" : ""),
                    FontSize = 11,
                    FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#ffffff" : "#94a3b8")),
                    VerticalAlignment = VerticalAlignment.Center
                };

                border.Child = tb;
                border.MouseDown += (s, e) =>
                {
                    ApplySuggestion(sug);
                };

                _suggestionPanel.Children.Add(border);
            }
        }

        private void ApplySuggestion(string suggestion)
        {
            if (suggestion.StartsWith(":"))
            {
                string code = suggestion.Split(' ')[0];
                string exp = SnippetExpansionEngine.Expand(code);
                _inputBox.Text = exp;
            }
            else if (suggestion.Contains("((") && suggestion.Contains("))"))
            {
                int lastBracket = Math.Max(_inputBox.Text.LastIndexOf("[["), _inputBox.Text.LastIndexOf("【【"));
                if (lastBracket >= 0)
                {
                    string before = _inputBox.Text.Substring(0, lastBracket);
                    _inputBox.Text = before + suggestion + " ";
                }
                else
                {
                    _inputBox.Text = suggestion + " ";
                }
            }
            else
            {
                _inputBox.Text = suggestion + " ";
            }
            _inputBox.CaretIndex = _inputBox.Text.Length;
            _suggestions.Clear();
            RenderSuggestions();
        }

        private void SaveStickyContent()
        {
            string txt = _inputBox.Text.Trim();
            if (string.IsNullOrEmpty(txt)) return;

            if (!string.IsNullOrEmpty(_stickyBlockId))
            {
                _statusBadgeText.Text = "⏳ 同步中...";
                ThreadPool.QueueUserWorkItem(async s =>
                {
                    try
                    {
                        var res = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                        {
                            id = _stickyBlockId,
                            dataType = "markdown",
                            data = txt
                        });
                        Dispatcher.Invoke(() =>
                        {
                            SoundPlayerHelper.PlayFeedback();
                            _statusBadgeText.Text = "✓ 已同步至思源 (" + DateTime.Now.ToString("HH:mm:ss") + ")";
                        });
                    }
                    catch
                    {
                        Dispatcher.Invoke(() => { _statusBadgeText.Text = "⚠️ 同步超时"; });
                    }
                });
            }
            else
            {
                _statusBadgeText.Text = "⏳ 记入日记中...";
                ThreadPool.QueueUserWorkItem(s =>
                {
                    try
                    {
                        var ctx = new DesktopStepContext("SYS", txt);
                        SmartCaptureMain.Exec(ctx);
                        Dispatcher.Invoke(() =>
                        {
                            SoundPlayerHelper.PlayFeedback();
                            _statusBadgeText.Text = "✓ 已追加至今日日记 (" + DateTime.Now.ToString("HH:mm:ss") + ")";
                        });
                    }
                    catch
                    {
                        Dispatcher.Invoke(() => { _statusBadgeText.Text = "⚠️ 写入日记失败"; });
                    }
                });
            }
        }

        public static void Prompt(Action<string> onSubmitted)
        {
            Prompt(null, onSubmitted);
        }

        public static void Prompt(ContextSnapshot snapshot, Action<string> onSubmitted)
        {
            var win = new MiniInputWindow(snapshot);
            win.OnTextSubmitted += onSubmitted;
            win.Show();
            win.Activate();
            win.Focus();
        }

        /// <summary>
        /// 呼出极轻量桌面图钉便签 (Sticky Pin HUD)，供写代码/写报告时置顶悬浮对照参考
        /// 支持 Ctrl+S 实时双向同步更新至思源笔记原生块或追加至日记，Ctrl+/- 缩放阅读字号
        /// </summary>
        public static void ShowStickyNote(string title, string content, string blockId = "")
        {
            var win = new MiniInputWindow(null);
            win._isStickyMode = true;
            win._stickyBlockId = blockId ?? "";
            win.Title = "思源外骨骼 · 桌面图钉便签: " + (string.IsNullOrEmpty(title) ? "参考便签" : title);
            win.Width = 480;
            win.Height = 280;
            win.Opacity = 0.96;
            win._rootBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f172ae6"));
            win.ExpandToScratchpadMode();
            win._intentLabelText.Text = "图钉便签";
            win._intentIconText.Text = "📌";
            win._intentPill.Cursor = Cursors.Hand;
            win._intentPill.ToolTip = "点击切换是否置顶悬浮 (当前: 置顶)";
            win._inputBox.Text = (!string.IsNullOrEmpty(title) ? ("### 📌 " + title + "\n\n") : "") + (content ?? "");
            win._statusBadgeText.Text = "📌 置顶 (Ctrl+S同步 · Esc退出)";
            win._statusBadge.ToolTip = "点击或按 Ctrl+S 实时同步更新至思源笔记";

            // 智能剪贴板管道监听：Ctrl+V 粘贴时自动清洗排版与公式保护
            win._inputBox.PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.V)
                {
                    try
                    {
                        if (System.Windows.Forms.Clipboard.ContainsText())
                        {
                            string clip = System.Windows.Forms.Clipboard.GetText();
                            if (!string.IsNullOrEmpty(clip))
                            {
                                string cleaned = TextCleaner.HarmonizeMarkdownSyntax(TextCleaner.FormatPanguSpacing(clip));
                                int c = win._inputBox.CaretIndex;
                                win._inputBox.Text = win._inputBox.Text.Insert(c, cleaned);
                                win._inputBox.CaretIndex = c + cleaned.Length;
                                win._statusBadgeText.Text = "✨ 智能粘贴清洗完成";
                                e.Handled = true;
                            }
                        }
                    }
                    catch { }
                }
            };

            var toDailyBtn = win.CreateHeaderActionButton("📥 投日记", "将便签当前内容作为小节沉淀入今日日记", () =>
            {
                string txt = win._inputBox.Text.Trim();
                if (!string.IsNullOrEmpty(txt))
                {
                    var ctx = new DesktopStepContext("SYS", txt);
                    ctx.SetVarValue("来源应用", "思源外骨骼 · 图钉便签");
                    SmartCaptureMain.Exec(ctx);
                    win._statusBadgeText.Text = "✓ 已存入今日日记！";
                    SoundPlayerHelper.PlayFeedback();
                }
            });

            var toSnippetsBtn = win.CreateHeaderActionButton("⭐ 存常用", "将便签内容保存为小驴常用语/快捷模板", () =>
            {
                string txt = win._inputBox.Text.Trim();
                if (!string.IsNullOrEmpty(txt))
                {
                    string safeTitle = !string.IsNullOrEmpty(title) ? title : "便签模板";
                    SnippetExpansionEngine.AddCustomSnippet(":" + Math.Abs(safeTitle.GetHashCode() % 1000), safeTitle, txt);
                    win._statusBadgeText.Text = "✓ 已加入常用模板！";
                    SoundPlayerHelper.PlayFeedback();
                }
            });

            var headerPanel = win._rootBorder.Child as Panel;
            if (headerPanel != null && headerPanel.Children.Count > 0 && headerPanel.Children[0] is Grid)
            {
                var topGrid = (Grid)headerPanel.Children[0];
                if (topGrid.Children.Count > 2 && topGrid.Children[2] is StackPanel)
                {
                    var actStack = (StackPanel)topGrid.Children[2];
                    actStack.Children.Insert(0, toDailyBtn);
                    actStack.Children.Insert(1, toSnippetsBtn);
                }
            }

            win.Topmost = true;
            win.Show();
            win.Activate();
        }
    }
}
