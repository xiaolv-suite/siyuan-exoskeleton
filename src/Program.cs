using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using XiaoLv.Desktop.Core;
using XiaoLv.Desktop.UI;
using MessageBox = System.Windows.MessageBox;

namespace XiaoLv.Desktop
{
    public static class Program
    {
        private static string MutexName
        {
            get
            {
                var isIsolated = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIYUAN_URL"));
                return isIsolated ? "XiaoLvDesktop_Isolated_Mutex" : "XiaoLvDesktop_SingleInstance_Mutex";
            }
        }
        private static Mutex _mutex;
        private static SystemTray _tray;
        private static HotKeyManager _hotkeys;
        private static IpcManager.Server _ipcServer;

        private static BentoWindow _bentoWindow;
        private static CapsuleWindow _capsuleWindow;
        private static DialogWindow _dialogWindow;
        private static System.Windows.Application _wpfApp;

        [STAThread]
        public static void Main(string[] args)
        {
            // 0. 初始化内嵌程序集与原生资源自释放解析器 (零外部松散依赖，单 EXE 自包含)
            EmbeddedResourceManager.InitializeAssemblyResolver();
            EmbeddedResourceManager.EnsureRuntimeAssets();

            // 1. 协议解析或参数规范化
            string incomingCommand = ParseCommandLineOrProtocol(args);

            // 2. 单实例检查 (防崩溃锁遗留导致互斥异常；支持多轮重试防残留句柄抢占)
            bool isNewInstance = false;
            for (int r = 0; r < 5; r++)
            {
                try
                {
                    _mutex = new Mutex(true, MutexName, out isNewInstance);
                    if (isNewInstance) break;
                }
                catch (AbandonedMutexException)
                {
                    isNewInstance = true;
                    break;
                }
                Thread.Sleep(150);
            }

            if (!isNewInstance)
            {
                // 已有实例在运行：将参数通过命名管道直接转交给运行中的实例
                string msgToSend = !string.IsNullOrEmpty(incomingCommand) ? incomingCommand : "open_panel";
                IpcManager.SendToRunningInstance(msgToSend);
                return;
            }

            // 3. 首次启动主进程
            _wpfApp = new System.Windows.Application();
            _wpfApp.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _wpfApp.DispatcherUnhandledException += (s, e) =>
            {
                Logger.Error("WPF Dispatcher exception: " + e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Logger.Error("AppDomain unhandled exception: " + e.ExceptionObject);
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => ExitApplication();

            // 异步注册私有协议与右键菜单（非阻塞后台执行）
            ThreadPool.QueueUserWorkItem(s =>
            {
                try
                {
                    ProtocolRegistrar.RegisterProtocol();
                    ShellContextMenuManager.Register();
                }
                catch { }
            });

            // 启动 IPC 管道服务监听
            _ipcServer = new IpcManager.Server();
            _ipcServer.OnMessageReceived += HandleIpcMessage;
            _ipcServer.Start();

            // 初始化窗口并在消息循环启动后立即离屏预热 WebView2
            _bentoWindow = new BentoWindow();
            _capsuleWindow = new CapsuleWindow();
            _dialogWindow = new DialogWindow();

            _wpfApp.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _bentoWindow.PrewarmAsync();
                    MemoryOptimizer.ScheduleTrim(3000);
                }
                catch { }
            }));

            _bentoWindow.OnSubActionRequested += (code, text) =>
            {
                DispatchInternal(code, text);
            };
            _bentoWindow.OnNotificationRequested += (t, m) => SafeNotify(t, m);
            _capsuleWindow.OnNotificationRequested += (t, m) => SafeNotify(t, m);

            _bentoWindow.OnSaveSettingsRequested += (json) =>
            {
                try
                {
                    var s = AppSettings.Instance;
                    var dict = JsonHelper.ParseDict(json);

                    s.MasterHotKey = JsonHelper.GetString(dict, "masterHotKey", s.MasterHotKey);
                    s.PenHotKey = JsonHelper.GetString(dict, "penHotKey", s.PenHotKey);
                    s.OcrHotKey = JsonHelper.GetString(dict, "ocrHotKey", s.OcrHotKey);
                    s.QuickHotKey = JsonHelper.GetString(dict, "quickHotKey", s.QuickHotKey);
                    s.FsrsHotKey = JsonHelper.GetString(dict, "fsrsHotKey", s.FsrsHotKey);
                    s.ClipHotKey = JsonHelper.GetString(dict, "clipHotKey", s.ClipHotKey);
                    s.PreferredSnippingTool = JsonHelper.GetString(dict, "preferredSnippingTool", s.PreferredSnippingTool);
                    s.PreferredSearchEngine = JsonHelper.GetString(dict, "preferredSearchEngine", s.PreferredSearchEngine);
                    s.DailyNoteTargetHeading = JsonHelper.GetString(dict, "dailyNoteTargetHeading", s.DailyNoteTargetHeading);
                    s.QuickNoteContinuationSec = JsonHelper.GetInt(dict, "quickNoteContinuationSec", s.QuickNoteContinuationSec);
                    s.QuickNoteTargetDoc = JsonHelper.GetString(dict, "quickNoteTargetDoc", s.QuickNoteTargetDoc);
                    s.AutoFloatEnabled = JsonHelper.GetBool(dict, "autoFloatEnabled", s.AutoFloatEnabled);
                    s.AutoStart = JsonHelper.GetBool(dict, "autoStart", s.AutoStart);
                    s.SoundFeedbackEnabled = JsonHelper.GetBool(dict, "soundFeedbackEnabled", s.SoundFeedbackEnabled);
                    s.LifelogAutoSectionEnabled = JsonHelper.GetBool(dict, "lifelogAutoSectionEnabled", s.LifelogAutoSectionEnabled);
                    s.WebClipDefaultMode = JsonHelper.GetString(dict, "webClipDefaultMode", s.WebClipDefaultMode);
                    s.WebClipTargetFolder = JsonHelper.GetString(dict, "webClipTargetFolder", s.WebClipTargetFolder);
                    s.WebClipCustomTemplate = JsonHelper.GetString(dict, "webClipCustomTemplate", s.WebClipCustomTemplate);
                    s.PinnedDocsJson = JsonHelper.GetString(dict, "pinnedDocsJson", s.PinnedDocsJson);
                    s.AiEnabled = JsonHelper.GetBool(dict, "aiEnabled", s.AiEnabled);
                    s.AiBaseUrl = JsonHelper.GetString(dict, "aiBaseUrl", s.AiBaseUrl);
                    s.AiModel = JsonHelper.GetString(dict, "aiModel", s.AiModel);
                    s.AiApiKey = JsonHelper.GetString(dict, "aiApiKey", s.AiApiKey);
                    s.CustomSnippetsJson = JsonHelper.GetString(dict, "customSnippetsJson", s.CustomSnippetsJson);
                    SnippetExpansionEngine.ReloadCustomSnippets(s.CustomSnippetsJson);

                    s.Save();
                    SelectionHelper.AutoFloatEnabled = s.AutoFloatEnabled;
                    SystemTray.SetAutoStart(s.AutoStart);
                    _hotkeys.RegisterConfiguredHotKeys(s.MasterHotKey, s.PenHotKey, s.OcrHotKey, s.QuickHotKey, s.FsrsHotKey, s.ClipHotKey);
                    SafeNotify("思源外骨骼", "✓ 桌面助手偏好设置已更新并立即生效！");
                }
                catch { }
            };

            _capsuleWindow.OnCapsuleCommand += (op, text) =>
            {
                // 随身划笔分流
                var ctx = new DesktopStepContext(op, text);
                ctx.OnNotificationRequested += (t, m) => SafeNotify(t, m);
                ContextPenMain.Exec(ctx);
            };

            _capsuleWindow.OnCapsuleCommandWithImage += (op, text, imgPath) =>
            {
                var ctx = new DesktopStepContext(op, text);
                if (!string.IsNullOrEmpty(imgPath)) ctx.SetVarValue("截图路径", imgPath);
                ctx.OnNotificationRequested += (t, m) => SafeNotify(t, m);
                ContextPenMain.Exec(ctx);
            };

            _capsuleWindow.OnOpenMasterRequested += (text) =>
            {
                _bentoWindow.ShowPanel();
            };

            // 载入持久化配置
            var settings = AppSettings.Instance;
            SelectionHelper.AutoFloatEnabled = settings.AutoFloatEnabled;

            // 智能剪贴板感知监听 (URL/表格/代码提纯)
            ClipboardWatcher.IsEnabled = settings.SmartClipboardEnabled;
            ClipboardWatcher.Start();

            // 启动思源内核健康守护与离岸队列监听
            SiYuanGuardian.NotifyCallback = (t, m) => SafeNotify(t, m);
            SiYuanGuardian.Start();

            // 全局快捷键注册
            _hotkeys = new HotKeyManager();
            _hotkeys.OnMasterPanelRequested += () =>
            {
                _wpfApp.Dispatcher.Invoke(() =>
                {
                    _bentoWindow.ShowPanel();
                });
            };

            _hotkeys.OnContextPenRequested += () =>
            {
                _wpfApp.Dispatcher.Invoke(() =>
                {
                    // 抓取当前选中文本
                    string selected = SelectionHelper.CaptureSelectedText(250);
                    if (string.IsNullOrEmpty(selected) && ClipboardWatcher.IsEnabled && ClipboardWatcher.LatestItem != null)
                    {
                        var clip = ClipboardWatcher.LatestItem;
                        if (!string.IsNullOrEmpty(clip.EnrichedMarkdown))
                        {
                            selected = clip.EnrichedMarkdown;
                        }
                    }
                    _capsuleWindow.ShowNearCursor(selected);
                });
            };

            _hotkeys.OnSnipOcrRequested += () => StartSnippingAndOcr();
            _hotkeys.OnQuickNoteRequested += () => PromptQuickCapture();
            _hotkeys.OnFsrsReviewRequested += () => StartFsrsReview();
            _hotkeys.OnWebClipRequested += () => StartWebClip();

            bool hotkeysOk = _hotkeys.RegisterConfiguredHotKeys(settings.MasterHotKey, settings.PenHotKey, settings.OcrHotKey, settings.QuickHotKey, settings.FsrsHotKey, settings.ClipHotKey);

            // 智能划选鼠标微控 (PopClip 模式支持)
            SelectionHelper.StartMouseHook();
            SelectionHelper.OnAutoSelectionDetected += (selected) =>
            {
                _wpfApp.Dispatcher.Invoke(() =>
                {
                    _capsuleWindow.ShowNearCursor(selected);
                });
            };

            // Office 本地文档联动热重载监听
            OfficeFileWatcher.NotificationHandler = (title, text) => SafeNotify(title, text);
            OfficeFileWatcher.OnOfficeFileChanged += (filePath) =>
            {
                if (_wpfApp != null && _wpfApp.Dispatcher != null && _bentoWindow != null)
                {
                    _wpfApp.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _bentoWindow.NotifyOfficeFileChanged(filePath);
                    }));
                }
            };

            // 系统托盘初始化
            _tray = new SystemTray();
            _tray.OnOpenPanelClicked += () => _wpfApp.Dispatcher.Invoke(() => _bentoWindow.ShowPanel());
            _tray.OnOpenPenClicked += () => _wpfApp.Dispatcher.Invoke(() =>
            {
                string selected = SelectionHelper.CaptureSelectedText(200);
                _capsuleWindow.ShowNearCursor(selected);
            });
            _tray.OnSnippingOcrClicked += () => StartSnippingAndOcr();
            _tray.OnQuickCaptureClicked += () => PromptQuickCapture();
            _tray.OnFsrsReviewClicked += () => StartFsrsReview();
            _tray.OnWebClipClicked += () => StartWebClip();
            _tray.OnManualSyncClicked += () =>
            {
                ThreadPool.QueueUserWorkItem(async s =>
                {
                    var res = await SiYuanClient.PostAsync("/api/sync/performSync", new { });
                    bool ok = res != null && res.Success;
                    if (ok) SoundPlayerHelper.PlayFeedback();
                    SafeNotify("思源外骨骼 · 云同步", ok ? "✓ 思源云端增量数据同步已完成！" : "⚠️ 同步响应超时或离线");
                });
            };
            _tray.OnInteractiveToastClicked += () => ShowInteractiveToast();
            _tray.OnCheckConnectionClicked += () => CheckSiYuanConnection();
            _tray.OnCheckUpdateClicked += () => PerformCheckUpdate(true);
            _tray.OnExitClicked += () => ExitApplication();
            _tray.Initialize(null);

            // 启动提示
            if (args.Length == 0 || !Array.Exists(args, a => a.Contains("autostart")))
            {
                SafeNotify("思源外骨骼 · 桌面穿透工作台", "已成功启动常驻后台！\n• Alt+Shift+Q 呼出总控\n• Alt+Shift+W 呼出划笔\n• Alt+Shift+G 网页剪藏");
            }

            // 处理首次启动传入的参数
            if (!string.IsNullOrEmpty(incomingCommand))
            {
                HandleIpcMessage(incomingCommand);
            }

            // 运行 WPF 消息循环
            _wpfApp.Run();
        }

        private static void HandleIpcMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg) || _wpfApp == null || _wpfApp.Dispatcher == null) return;

            _wpfApp.Dispatcher.BeginInvoke(new Action(() =>
            {
                var clean = msg.Trim();

                Logger.Info("HandleIpcMessage: " + clean);

                // 1. JSON 结构化信封优先解析
                if (clean.StartsWith("{") && clean.EndsWith("}"))
                {
                    var envelope = JsonHelper.ParseDict(clean);
                    string eCmd = JsonHelper.GetString(envelope, "cmd");
                    string eText = JsonHelper.GetString(envelope, "text");
                    if (string.IsNullOrEmpty(eText)) eText = JsonHelper.GetString(envelope, "param");
                    var extra = JsonHelper.GetDict(envelope, "extra");

                    if (!string.IsNullOrEmpty(eCmd))
                    {
                        if (eCmd.Equals("exit", StringComparison.OrdinalIgnoreCase))
                        {
                            _wpfApp.Dispatcher.Invoke(() => ExitApplication());
                            return;
                        }
                        if (eCmd.Equals("open_panel", StringComparison.OrdinalIgnoreCase) || eCmd.Equals("open", StringComparison.OrdinalIgnoreCase))
                        {
                            _bentoWindow.ShowPanel();
                            return;
                        }
                        if (eCmd.Equals("close_panel", StringComparison.OrdinalIgnoreCase) || eCmd.Equals("close", StringComparison.OrdinalIgnoreCase))
                        {
                            _bentoWindow.Hide();
                            return;
                        }
                        if (eCmd.Equals("trim_memory", StringComparison.OrdinalIgnoreCase) || eCmd.Equals("trim", StringComparison.OrdinalIgnoreCase))
                        {
                            MemoryOptimizer.PerformDeepTrim();
                            return;
                        }
                        DispatchInternal(eCmd, eText, extra);
                        return;
                    }
                }

                if (clean.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    _wpfApp.Dispatcher.Invoke(() => ExitApplication());
                    return;
                }

                if (clean.StartsWith("sendfile:", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = clean.Substring(9).Trim().Trim('"');
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        FileDispatcher.DispatchFileToSiYuan(filePath, (t, m) => SafeNotify(t, m));
                    });
                    return;
                }

                if (clean.Equals("open_panel", StringComparison.OrdinalIgnoreCase) || clean.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    _bentoWindow.ShowPanel();
                    return;
                }
                if (clean.Equals("open_pen", StringComparison.OrdinalIgnoreCase) || clean.Equals("pen", StringComparison.OrdinalIgnoreCase))
                {
                    string selected = SelectionHelper.CaptureSelectedText(200);
                    _capsuleWindow.ShowNearCursor(selected);
                    return;
                }

                if (clean.Equals("ocr", StringComparison.OrdinalIgnoreCase) || clean.Equals("syocr", StringComparison.OrdinalIgnoreCase))
                {
                    StartSnippingAndOcr();
                    return;
                }
                if (clean.Equals("quick", StringComparison.OrdinalIgnoreCase))
                {
                    PromptQuickCapture();
                    return;
                }
                if (clean.Equals("fsrs", StringComparison.OrdinalIgnoreCase) || clean.Equals("review", StringComparison.OrdinalIgnoreCase))
                {
                    StartFsrsReview();
                    return;
                }
                if (clean.Equals("toast", StringComparison.OrdinalIgnoreCase))
                {
                    ShowInteractiveToast();
                    return;
                }
                if (clean.Equals("webclip", StringComparison.OrdinalIgnoreCase) || clean.Equals("clip", StringComparison.OrdinalIgnoreCase) || clean.Equals("g", StringComparison.OrdinalIgnoreCase))
                {
                    StartWebClip();
                    return;
                }

                // 支持 siyuan-exoskeleton:// 与 xiaolv:// 外部协议或直接传递指令码
                var cmd = clean;
                string text = "";
                if (clean.StartsWith("siyuan-exoskeleton://", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("xiaolv://", StringComparison.OrdinalIgnoreCase))
                {
                    // 1. 本地 Office 双向深链穿透 (siyuan-exoskeleton://open-office 或 ://office)
                    if (clean.Contains("://open-office") || clean.Contains("://office"))
                    {
                        string p = ExtractQueryParam(clean, "path");
                        string sheet = ExtractQueryParam(clean, "sheet");
                        string cell = ExtractQueryParam(clean, "cell");
                        string heading = ExtractQueryParam(clean, "heading");
                        if (!string.IsNullOrEmpty(p))
                        {
                            ThreadPool.QueueUserWorkItem(s =>
                            {
                                XiaoLv.Desktop.Engine.Modules.OfficeDeepLinkMain.OpenOfficeLocation(p, sheet, cell, heading);
                            });
                            return;
                        }
                    }

                    // 2. 本地 PDF 页码精准穿透 (siyuan-exoskeleton://open-pdf 或 ://pdf)
                    if (clean.Contains("://open-pdf") || clean.Contains("://pdf"))
                    {
                        string p = ExtractQueryParam(clean, "path");
                        string pageStr = ExtractQueryParam(clean, "page");
                        int pageNum = 0;
                        int.TryParse(pageStr, out pageNum);
                        if (!string.IsNullOrEmpty(p))
                        {
                            ThreadPool.QueueUserWorkItem(s =>
                            {
                                FileDispatcher.OpenWithPreferredApp(p, pageNum);
                            });
                            return;
                        }
                    }

                    // 3. 通用本地文件秒开 (siyuan-exoskeleton://open-file 或 ://file)
                    if (clean.Contains("://open-file") || clean.Contains("://file"))
                    {
                        string p = ExtractQueryParam(clean, "path");
                        if (!string.IsNullOrEmpty(p))
                        {
                            ThreadPool.QueueUserWorkItem(s =>
                            {
                                FileDispatcher.OpenWithPreferredApp(p, 0);
                            });
                            return;
                        }
                    }

                    var m = Regex.Match(clean, @"[?&]cmd=([^&]+)");
                    if (m.Success) cmd = Uri.UnescapeDataString(m.Groups[1].Value);
                    else if (clean.Contains("://quicknote")) cmd = "quick_note";
                    else if (clean.Contains("://voice")) cmd = "voice";
                    else if (clean.Contains("://workspace") || clean.Contains("://ws")) cmd = "workspace";
                    else if (clean.Contains("://trim")) cmd = "trim_memory";
                    else cmd = "open_panel";

                    var mText = Regex.Match(clean, @"[?&]text=([^&]+)");
                    if (mText.Success) text = Uri.UnescapeDataString(mText.Groups[1].Value);
                }
                else
                {
                    int spaceIdx = clean.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
                    if (spaceIdx > 0)
                    {
                        cmd = clean.Substring(0, spaceIdx).Trim();
                        text = clean.Substring(spaceIdx + 1).Trim();
                    }
                }

                if (cmd == "open_panel")
                {
                    _bentoWindow.ShowPanel();
                }
                else
                {
                    DispatchInternal(cmd, text);
                }
            }));
        }

        private static void DispatchInternal(string cmd, string text)
        {
            DispatchInternal(cmd, text, null);
        }

        private static void DispatchInternal(string cmd, string text, Dictionary<string, object> extra)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    var ctx = new DesktopStepContext(cmd, text);
                    if (extra != null)
                    {
                        foreach (var kv in extra)
                        {
                            ctx.SetVarValue(kv.Key, kv.Value);
                        }
                    }
                    ctx.OnNotificationRequested += (t, m) => SafeNotify(t, m);
                    ctx.OnOpenUrlRequested += (url) =>
                    {
                        try { System.Diagnostics.Process.Start(url); } catch { }
                    };

                    // 弹窗交互闭环拦截
                    ctx.OnDialogRequested += (url, isInput) =>
                    {
                        _wpfApp.Dispatcher.Invoke(async () =>
                        {
                            var result = await _dialogWindow.ShowDialogAsync(url);
                            if (!string.IsNullOrEmpty(result))
                            {
                                var phase2Ctx = new DesktopStepContext(cmd, text);
                                phase2Ctx.SetVarValue("选中指令", result);
                                phase2Ctx.SetVarValue("格式指令", result);
                                phase2Ctx.SetVarValue("选中条目ID", result);
                                phase2Ctx.SetVarValue("选中标题ID", result);
                                phase2Ctx.SetVarValue("选中块ID", result);
                                phase2Ctx.SetVarValue("自定义输入", result);
                                phase2Ctx.SetVarValue("ask值", result);
                                phase2Ctx.SetVarValue("阶段", "checkin");
                                phase2Ctx.OnNotificationRequested += (t, m) => SafeNotify(t, m);
                                ActionDispatcher.Dispatch(cmd, phase2Ctx);
                            }
                        });
                    };

                    bool dispatched = ActionDispatcher.Dispatch(cmd, ctx);
                    if (!dispatched)
                    {
                        SafeNotify("思源外骨骼", "⚠️ 未识别调度指令: " + cmd);
                    }
                }
                catch (Exception ex)
                {
                    SafeNotify("思源外骨骼", "⚠️ 执行异常: " + ex.Message);
                }
            });
        }

        public static void SafeNotify(string title, string message)
        {
            try
            {
                if (_wpfApp != null && _wpfApp.Dispatcher != null && !_wpfApp.Dispatcher.HasShutdownStarted)
                {
                    _wpfApp.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (_tray != null) _tray.ShowNotice(title, message); } catch { }
                    }));
                }
                else
                {
                    if (_tray != null) _tray.ShowNotice(title, message);
                }
            }
            catch { }
        }

        public static void StartSnippingAndOcr()
        {
            SnippingManager.TriggerCapture((recognized, bmp) =>
            {
                string imagePath = "";
                if (bmp != null)
                {
                    try
                    {
                        var tmpFile = Path.Combine(Path.GetTempPath(), "xiaolv_snip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                        bmp.Save(tmpFile, System.Drawing.Imaging.ImageFormat.Png);
                        imagePath = tmpFile;
                    }
                    catch { }
                    try { bmp.Dispose(); } catch { }
                }

                if (!string.IsNullOrEmpty(recognized) || !string.IsNullOrEmpty(imagePath))
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(recognized))
                            System.Windows.Forms.Clipboard.SetText(recognized);
                    }
                    catch { }

                    _wpfApp.Dispatcher.Invoke(() =>
                    {
                        SafeNotify("思源外骨骼", "✓ 截图完成（" + (!string.IsNullOrEmpty(recognized) ? (recognized.Length + " 字") : "已捕获") + "），按 1 即可图文入日记！");
                        _capsuleWindow.ShowNearCursorWithImage(recognized, imagePath);
                    });
                }
                else
                {
                    _wpfApp.Dispatcher.Invoke(() =>
                    {
                        SafeNotify("思源外骨骼", "ℹ️ 截图已捕获或未识别到文本");
                    });
                }
            }, (title, msg) => SafeNotify(title, msg));
        }

        public static void PromptQuickCapture()
        {
            // 极速秒开：使用 <0.3ms 的纯原生探测瞬时弹出窗体
            var snapshot = ContextPerception.CaptureForegroundContextFast();

            _wpfApp.Dispatcher.Invoke(() =>
            {
                MiniInputWindow.Prompt(snapshot, text =>
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        var ctx = new DesktopStepContext("SYS", text);
                        if (snapshot != null)
                        {
                            ctx.SetVarValue("来源窗口", snapshot.FormattedCitation);
                            ctx.SetVarValue("来源应用", snapshot.AppDisplayName);
                            ctx.SetVarValue("来源标题", snapshot.CleanTitle);
                            ctx.SetVarValue("来源链接", snapshot.Url);
                        }
                        ctx.OnNotificationRequested += (t, m) => SafeNotify(t, m);
                        SmartCaptureMain.Exec(ctx);
                    }
                });
            });

            // 异步后台补全 Chromium 完整 URL（不卡顿输入窗毫秒弹出）
            if (snapshot != null && snapshot.Hwnd != IntPtr.Zero)
            {
                ThreadPool.QueueUserWorkItem(s =>
                {
                    try
                    {
                        var full = ContextPerception.CaptureForegroundContext(snapshot.Hwnd);
                        if (full != null && !string.IsNullOrEmpty(full.Url))
                        {
                            snapshot.Url = full.Url;
                        }
                    }
                    catch { }
                });
            }
        }

        public static void StartFsrsReview()
        {
            _wpfApp.Dispatcher.Invoke(async () =>
            {
                await FSRSCapsuleWindow.OpenAsync((t, m) => SafeNotify(t, m));
            });
        }

        public static void ShowMasterPanel(string initialQuery = null)
        {
            if (_wpfApp != null && _bentoWindow != null)
            {
                _wpfApp.Dispatcher.Invoke(() => _bentoWindow.ShowPanel(initialQuery));
            }
        }

        public static void StartWebClip()
        {
            string defaultMode = AppSettings.Instance.WebClipDefaultMode;
            if (string.IsNullOrEmpty(defaultMode)) defaultMode = "card";
            StartWebClip("", defaultMode);
        }

        public static void StartWebClip(string annotation, string mode)
        {
            if (string.IsNullOrEmpty(mode))
            {
                mode = AppSettings.Instance.WebClipDefaultMode;
                if (string.IsNullOrEmpty(mode)) mode = "card";
            }
            SmartCaptureMain.ExecuteWebClip((t, m) => SafeNotify(t, m), annotation, mode);
        }

        public static void ShowInteractiveToast()
        {
            InteractiveToastManager.ShowQuickCaptureToast((text, action) =>
            {
                if (!string.IsNullOrEmpty(text))
                {
                    string finalCmd = "SYS";
                    if (action != null && action.Contains("card")) text = "?" + text;
                    else if (action != null && action.Contains("todo")) text = "!" + text;
                    var ctx = new DesktopStepContext(finalCmd, text);
                    ctx.OnNotificationRequested += (t, m) => SafeNotify(t, m);
                    SmartCaptureMain.Exec(ctx);
                }
            });
        }

        private static void CheckSiYuanConnection()
        {
            new Thread(() =>
            {
                var ctx = new DesktopStepContext("L0-02");
                ctx.OnNotificationRequested += (t, m) => SafeNotify(t, m);
                PreflightMain.Exec(ctx);
            })
            { IsBackground = true }.Start();
        }

        private static void PerformCheckUpdate(bool showTipIfNone)
        {
            new Thread(() =>
            {
                var info = AutoUpdater.CheckForUpdate();
                if (info.HasUpdate)
                {
                    var res = MessageBox.Show(
                        "发现新版本 v" + info.LatestVersion + "！\n\n更新内容：\n" + info.Changelog + "\n\n是否立即前往下载？",
                        "思源外骨骼 · 自动更新",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information
                    );
                    if (res == MessageBoxResult.Yes && !string.IsNullOrEmpty(info.DownloadUrl))
                    {
                        try { System.Diagnostics.Process.Start(info.DownloadUrl); } catch { }
                    }
                }
                else if (showTipIfNone)
                {
                    SafeNotify("思源外骨骼", "当前已是最新版本 (v" + AutoUpdater.CURRENT_VERSION + ")！");
                }
            })
            { IsBackground = true }.Start();
        }

        private static string ParseCommandLineOrProtocol(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            var arg = args[0].Trim();
            if (arg.StartsWith("siyuan-exoskeleton://", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("xiaolv://", StringComparison.OrdinalIgnoreCase)) return arg;
            if ((arg.Equals("-sendfile", StringComparison.OrdinalIgnoreCase) || arg.Equals("-senddir", StringComparison.OrdinalIgnoreCase)) && args.Length > 1)
            {
                return "sendfile:" + args[1].Trim();
            }

            string cmd = "";
            string paramText = "";

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].Trim();
                if ((a.Equals("-cmd", StringComparison.OrdinalIgnoreCase) || a.Equals("-run", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    cmd = args[++i];
                }
                else if ((a.Equals("-param", StringComparison.OrdinalIgnoreCase) || a.Equals("-text", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    paramText = args[++i];
                }
                else if (a.StartsWith("--"))
                {
                    cmd = a.TrimStart('-');
                }
            }

            if (!string.IsNullOrEmpty(cmd))
            {
                return JsonHelper.ToJson(new { cmd = cmd, text = paramText });
            }

            return arg;
        }

        private static string ExtractQueryParam(string url, string paramName)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(paramName)) return "";
            try
            {
                var match = Regex.Match(url, @"[?&]" + Regex.Escape(paramName) + @"=([^&#]+)");
                if (match.Success)
                {
                    return Uri.UnescapeDataString(match.Groups[1].Value).Trim();
                }
            }
            catch { }
            return "";
        }

        private static void ExitApplication()
        {
            try
            {
                SiYuanGuardian.Stop();
                ClipboardWatcher.Stop();
                SelectionHelper.StopMouseHook();
                OfficeFileWatcher.StopAll();
                if (_hotkeys != null) _hotkeys.Dispose();
                if (_tray != null) _tray.Dispose();
                if (_ipcServer != null) _ipcServer.Dispose();
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }
            catch { }
            Environment.Exit(0);
        }
    }
}
