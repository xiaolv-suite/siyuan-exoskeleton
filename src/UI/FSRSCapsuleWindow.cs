using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    public class RiffCardItem
    {
        public string CardId { get; set; }
        public string DeckId { get; set; }
        public string DeckName { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
    }

    /// <summary>
    /// 桌面级 0ms 原生 FSRS 闪卡 3秒微刷卡微舱 (Alt+Shift+R)
    /// 深度联动思源原生 /api/riff/ 算法，无需切入思源大窗口，随时随地在桌面利用碎片时间清空待复习卡片
    /// </summary>
    public class FSRSCapsuleWindow : Window
    {
        private List<RiffCardItem> _cards = new List<RiffCardItem>();
        private int _currentIndex = 0;
        private bool _isAnswerFlipped = false;

        private TextBlock _progressText;
        private TextBlock _deckText;
        private TextBlock _questionText;
        private TextBlock _flipHintText;
        private Border _answerBox;
        private TextBlock _answerText;
        private StackPanel _ratingPanel;

        public FSRSCapsuleWindow()
        {
            Title = "思源外骨骼 · FSRS 闪卡微舱";
            Width = 540;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowPositionHelper.CenterOnCursorScreen(this);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;

            var rootBorder = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#090d16")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#a855f7")),
                BorderThickness = new Thickness(1.5),
                ClipToBounds = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 36,
                    ShadowDepth = 10,
                    Opacity = 0.80
                }
            };

            var mainGrid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Card Body
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Ratings

            // 1. 顶栏：卡组名 + 进度角标 + 关闭按钮
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel { Orientation = Orientation.Horizontal };
            var badgeLogo = new TextBlock
            {
                Text = "🧠",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _deckText = new TextBlock
            {
                Text = "FSRS 记忆卡组",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f8fafc")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            _progressText = new TextBlock
            {
                Text = "第 0/0 张",
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#c084fc")),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3b0764")),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerLeft.Children.Add(badgeLogo);
            headerLeft.Children.Add(_deckText);
            headerLeft.Children.Add(_progressText);
            Grid.SetColumn(headerLeft, 0);
            headerGrid.Children.Add(headerLeft);

            var closeBtn = new Button
            {
                Content = "✕",
                Width = 24,
                Height = 24,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94a3b8")),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12
            };
            closeBtn.Click += (s, e) => Close();
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);

            // 顶栏支持鼠标左键拖拽移动视窗
            headerGrid.Background = Brushes.Transparent;
            headerGrid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            Grid.SetRow(headerGrid, 0);
            mainGrid.Children.Add(headerGrid);

            // 2. 主体卡片区域 (问题区 + 答案区)
            var cardScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 12, 0, 12)
            };
            var cardBody = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            _questionText = new TextBlock
            {
                Text = "正在加载到期卡片...",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f8fafc")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(4, 8, 4, 12)
            };
            cardBody.Children.Add(_questionText);

            _flipHintText = new TextBlock
            {
                Text = "[ 按空格键或点击卡片翻看答案 ]",
                FontSize = 11.5,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748b")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8),
                Cursor = Cursors.Hand
            };
            _flipHintText.MouseLeftButtonDown += (s, e) => FlipCard();
            cardBody.Children.Add(_flipHintText);

            _answerBox = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#13192b")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _answerText = new TextBlock
            {
                Text = "",
                FontSize = 13.5,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38bdf8")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };
            _answerBox.Child = _answerText;
            cardBody.Children.Add(_answerBox);

            cardScroll.Content = cardBody;
            Grid.SetRow(cardScroll, 1);
            mainGrid.Children.Add(cardScroll);

            // 3. 底部评级按键组 (1~4 评分)
            _ratingPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed
            };

            _ratingPanel.Children.Add(CreateRatingBtn("1 重来", "#e11d48", Key.D1, () => SubmitRating(1)));
            _ratingPanel.Children.Add(CreateRatingBtn("2 困难", "#d97706", Key.D2, () => SubmitRating(2)));
            _ratingPanel.Children.Add(CreateRatingBtn("3 良好", "#059669", Key.D3, () => SubmitRating(3)));
            _ratingPanel.Children.Add(CreateRatingBtn("4 简单", "#0284c7", Key.D4, () => SubmitRating(4)));

            Grid.SetRow(_ratingPanel, 2);
            mainGrid.Children.Add(_ratingPanel);

            rootBorder.Child = mainGrid;
            Content = rootBorder;

            // 窗口关闭即时触发物理内存深度压缩与垃圾回收
            Closed += (s, e) => MemoryOptimizer.ScheduleTrim(300);

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Space)
                {
                    if (!_isAnswerFlipped)
                    {
                        FlipCard();
                        e.Handled = true;
                    }
                }
                else if (_isAnswerFlipped)
                {
                    if (e.Key == Key.D1 || e.Key == Key.NumPad1) { SubmitRating(1); e.Handled = true; }
                    else if (e.Key == Key.D2 || e.Key == Key.NumPad2) { SubmitRating(2); e.Handled = true; }
                    else if (e.Key == Key.D3 || e.Key == Key.NumPad3) { SubmitRating(3); e.Handled = true; }
                    else if (e.Key == Key.D4 || e.Key == Key.NumPad4) { SubmitRating(4); e.Handled = true; }
                }
            };
        }

        private Border CreateRatingBtn(string label, string colorHex, Key shortcutKey, Action onClick)
        {
            var btnBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(6, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            var txt = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            btnBorder.Child = txt;
            btnBorder.MouseLeftButtonDown += (s, e) => onClick();
            return btnBorder;
        }

        private void FlipCard()
        {
            if (_isAnswerFlipped) return;
            _isAnswerFlipped = true;
            _flipHintText.Visibility = Visibility.Collapsed;
            _answerBox.Visibility = Visibility.Visible;
            _ratingPanel.Visibility = Visibility.Visible;
        }

        private void SubmitRating(int rating)
        {
            if (_cards.Count == 0 || _currentIndex >= _cards.Count) return;
            var current = _cards[_currentIndex];

            // 异步后台提交评级至思源原生 FSRS
            Task.Run(async () =>
            {
                try
                {
                    await SiYuanClient.PostAsync("/api/riff/reviewRiffCard", new
                    {
                        deckID = current.DeckId,
                        cardID = current.CardId,
                        rating = rating
                    });
                }
                catch { }
            });

            _currentIndex++;
            ShowCurrentCard();
        }

        private void ShowCurrentCard()
        {
            if (_currentIndex >= _cards.Count)
            {
                // 全量复习完毕
                _progressText.Text = "已完成";
                _questionText.Text = "🎉 太棒了！今日待复习卡片已全部清空！";
                _questionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10b981"));
                _flipHintText.Visibility = Visibility.Collapsed;
                _answerBox.Visibility = Visibility.Collapsed;
                _ratingPanel.Visibility = Visibility.Collapsed;

                Task.Delay(1400).ContinueWith(t =>
                {
                    Dispatcher.Invoke(() => Close());
                });
                return;
            }

            var card = _cards[_currentIndex];
            _progressText.Text = string.Format("第 {0}/{1} 张", _currentIndex + 1, _cards.Count);
            _deckText.Text = !string.IsNullOrEmpty(card.DeckName) ? card.DeckName : "FSRS 卡片";
            _questionText.Text = card.Question;
            _questionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f8fafc"));
            _answerText.Text = card.Answer;

            _isAnswerFlipped = false;
            _flipHintText.Visibility = Visibility.Visible;
            _answerBox.Visibility = Visibility.Collapsed;
            _ratingPanel.Visibility = Visibility.Collapsed;
        }

        public static async Task OpenAsync(Action<string, string> notify)
        {
            var win = new FSRSCapsuleWindow();
            win._progressText.Text = "准备中...";
            win._questionText.Text = "⚡ 正在从思源知识库检索待复习卡片...";
            win._flipHintText.Text = "连通 FSRS 算法内核中...";
            win.Show();
            win.Activate();
            win.Focus();

            await Task.Run(async () =>
            {
                try
                {
                    // 1. 查询待复习卡片
                    var res = await SiYuanClient.PostAsync("/api/riff/getRiffDueCards", new { deckID = "" });
                    var cardList = new List<RiffCardItem>();

                    if (res.Success && res.Data != null)
                    {
                        var dict = res.Data as Dictionary<string, object>;
                        var cards = JsonHelper.GetList(dict, "cards");
                        if (cards != null && cards.Count > 0)
                        {
                            foreach (var cObj in cards)
                            {
                                var c = cObj as Dictionary<string, object>;
                                if (c == null) continue;

                                string cardId = JsonHelper.GetString(c, "cardID");
                                string deckId = JsonHelper.GetString(c, "deckID");
                                if (string.IsNullOrEmpty(cardId)) cardId = JsonHelper.GetString(c, "id");

                                string q = "卡片内容";
                                string a = "暂无答案";

                                var blockRes = await SiYuanClient.PostAsync("/api/block/getBlockKramdown", new { id = cardId });
                                if (blockRes.Success && blockRes.Data != null)
                                {
                                    var bDict = blockRes.Data as Dictionary<string, object>;
                                    var kramdown = JsonHelper.GetString(bDict, "kramdown");
                                    if (!string.IsNullOrEmpty(kramdown))
                                    {
                                        var splitIdx = kramdown.IndexOf("//");
                                        if (splitIdx > 0)
                                        {
                                            q = kramdown.Substring(0, splitIdx).Trim().TrimStart('?', '？').Trim();
                                            a = kramdown.Substring(splitIdx + 2).Trim();
                                        }
                                        else
                                        {
                                            q = kramdown.Trim().TrimStart('?', '？').Trim();
                                            a = "请在思源中查看完整上下文";
                                        }
                                    }
                                }

                                cardList.Add(new RiffCardItem
                                {
                                    CardId = cardId,
                                    DeckId = deckId,
                                    Question = q,
                                    Answer = a
                                });
                            }
                        }
                    }

                    win.Dispatcher.Invoke(() =>
                    {
                        win._cards = cardList;
                        win._currentIndex = 0;
                        if (cardList.Count == 0)
                        {
                            win._progressText.Text = "已完成";
                            win._questionText.Text = "🎉 太棒了！今日待复习卡片已全部清空！";
                            win._questionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10b981"));
                            win._flipHintText.Visibility = Visibility.Collapsed;
                            Task.Delay(1400).ContinueWith(t => win.Dispatcher.Invoke(() => win.Close()));
                        }
                        else
                        {
                            win.ShowCurrentCard();
                        }
                    });
                }
                catch (Exception ex)
                {
                    win.Dispatcher.Invoke(() =>
                    {
                        win._questionText.Text = "⚠️ 加载闪卡失败: " + ex.Message;
                    });
                }
            });
        }
    }
}
