using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using XiaoLv.Desktop.UI;

namespace XiaoLv.Desktop.Core
{
    public static class SnippingManager
    {
        public static void TriggerCapture(Action<string, Bitmap> onCaptured, Action<string, string> showNotice)
        {
            var pref = AppSettings.Instance.PreferredSnippingTool;
            var tool = ToolDetector.GetBestTool(pref);

            if (tool.Id == "builtin" || (string.IsNullOrEmpty(tool.ExePath) && tool.Id != "windows"))
            {
                // 使用小驴内置原生截屏与离线 OCR
                LaunchBuiltinSnipper(onCaptured, showNotice);
                return;
            }

            if (tool.Id == "windows")
            {
                if (showNotice != null) showNotice("思源外骨骼", "已唤起 Windows 系统截图，截图完成后自动捕获入库...");
                try
                {
                    Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
                    WatchClipboardForExternalSnip(onCaptured, showNotice);
                }
                catch
                {
                    LaunchBuiltinSnipper(onCaptured, showNotice);
                }
                return;
            }

            if (tool.Id == "pixpin")
            {
                if (showNotice != null) showNotice("思源外骨骼", "已调动本地 PixPin 截图，截图或OCR完成后自动流转入思源...");
                try
                {
                    // 若 PixPin 已经在运行，调用其 CLI 参数 -s 触发截图
                    if (File.Exists(tool.ExePath))
                    {
                        Process.Start(new ProcessStartInfo(tool.ExePath, "-s") { UseShellExecute = true });
                    }
                    WatchClipboardForExternalSnip(onCaptured, showNotice);
                }
                catch
                {
                    LaunchBuiltinSnipper(onCaptured, showNotice);
                }
                return;
            }

            if (tool.Id == "snipaste")
            {
                if (showNotice != null) showNotice("思源外骨骼", "已调动本地 Snipaste 截图，截图完成后自动流转入思源...");
                try
                {
                    if (File.Exists(tool.ExePath))
                    {
                        Process.Start(new ProcessStartInfo(tool.ExePath, "snip") { UseShellExecute = true });
                    }
                    WatchClipboardForExternalSnip(onCaptured, showNotice);
                }
                catch
                {
                    LaunchBuiltinSnipper(onCaptured, showNotice);
                }
                return;
            }

            // 兜底
            LaunchBuiltinSnipper(onCaptured, showNotice);
        }

        private static void LaunchBuiltinSnipper(Action<string, Bitmap> onCaptured, Action<string, string> showNotice)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var snipper = new SnippingOverlayWindow();
                snipper.OnCaptureFailed += (message) =>
                {
                    if (showNotice != null) showNotice("思源外骨骼 · 截图失败", message);
                };
                snipper.OnRegionCaptured += (bmp) =>
                {
                    if (showNotice != null) showNotice("思源外骨骼 · 离线 OCR", "正在通过本地硬件加速引擎识别文字...");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        bool ownershipTransferred = false;
                        try
                        {
                            string recognized = LocalOcrEngine.RecognizeBitmap(bmp);
                            if (onCaptured == null)
                            {
                                if (showNotice != null) showNotice("思源外骨骼 · 截图失败", "截图已捕获，但当前没有后续处理器。");
                                return;
                            }
                            onCaptured(recognized, bmp);
                            ownershipTransferred = true;
                        }
                        catch (Exception ex)
                        {
                            if (showNotice != null) showNotice("思源外骨骼 · 截图失败", "本地 OCR 或截图处理失败：" + ex.Message);
                        }
                        finally
                        {
                            if (!ownershipTransferred && bmp != null) bmp.Dispose();
                        }
                    });
                };
                snipper.Show();
            });
        }

        private static void WatchClipboardForExternalSnip(Action<string, Bitmap> onCaptured, Action<string, string> showNotice)
        {
            // 使用独立 STA 后台线程监听剪贴板 30 秒内的产生物（WinForms Clipboard 必须在 STA 线程访问）
            var watcherThread = new Thread(() =>
            {
                int start = Environment.TickCount;
                bool captureReceived = false;
                string initialText = "";
                try { if (Clipboard.ContainsText()) initialText = Clipboard.GetText(); } catch { }

                while (Environment.TickCount - start < 30000)
                {
                    Thread.Sleep(300);
                    try
                    {
                        // 1. 如果产生了图片
                        if (Clipboard.ContainsImage())
                        {
                            using (var img = Clipboard.GetImage())
                            {
                                if (img != null)
                                {
                                    var bmp = new Bitmap(img);
                                    bool ownershipTransferred = false;
                                    try
                                    {
                                        string ocr = LocalOcrEngine.RecognizeBitmap(bmp);
                                        if (onCaptured != null)
                                        {
                                            onCaptured(ocr, bmp);
                                            ownershipTransferred = true;
                                        }
                                        else if (showNotice != null)
                                        {
                                            showNotice("思源外骨骼 · 截图失败", "截图已捕获，但当前没有后续处理器。");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        if (showNotice != null) showNotice("思源外骨骼 · 截图失败", "截图处理失败：" + ex.Message);
                                    }
                                    finally
                                    {
                                        if (!ownershipTransferred) bmp.Dispose();
                                    }
                                    captureReceived = true;
                                    return;
                                }
                            }
                        }

                        // 2. 如果产生了新文本 (如 PixPin 提取文字)
                        if (Clipboard.ContainsText())
                        {
                            var currentText = Clipboard.GetText();
                            if (!string.IsNullOrEmpty(currentText) && currentText != initialText)
                            {
                                if (onCaptured != null) onCaptured(currentText, null);
                                captureReceived = true;
                                return;
                            }
                        }
                    }
                    catch { }
                }
                if (!captureReceived && showNotice != null)
                {
                    showNotice("思源外骨骼 · 截图未完成", "30 秒内没有检测到新截图或识别文本，请重新截图后重试。");
                }
            });
            watcherThread.IsBackground = true;
            watcherThread.SetApartmentState(ApartmentState.STA);
            watcherThread.Start();
        }
    }
}
