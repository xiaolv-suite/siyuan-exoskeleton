using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XiaoLv.Desktop.Core
{
    public static class ProtocolRegistrar
    {
        /// <summary>
        /// 将 siyuan-exoskeleton:// 与 xiaolv:// 协议注册到当前用户注册表
        /// </summary>
        public static void RegisterProtocol()
        {
            RegisterProtocolNamed("siyuan-exoskeleton");
            RegisterProtocolNamed("xiaolv");
        }

        private static void RegisterProtocolNamed(string protocolName)
        {
            try
            {
                var exePath = Application.ExecutablePath;
                string regRoot = @"Software\Classes\" + protocolName;
                using (var key = Registry.CurrentUser.CreateSubKey(regRoot))
                {
                    if (key == null) return;
                    key.SetValue("", "URL:SiYuan Exoskeleton Protocol");
                    key.SetValue("URL Protocol", "");

                    using (var defIcon = key.CreateSubKey("DefaultIcon"))
                    {
                        if (defIcon != null) defIcon.SetValue("", "\"" + exePath + "\",0");
                    }

                    using (var shellKey = key.CreateSubKey(@"shell\open\command"))
                    {
                        if (shellKey != null) shellKey.SetValue("", "\"" + exePath + "\" \"%1\"");
                    }
                }
            }
            catch { }
        }

        public static bool IsProtocolRegistered()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\siyuan-exoskeleton", false))
                {
                    if (key != null) return true;
                }
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\xiaolv", false))
                {
                    return key != null;
                }
            }
            catch { return false; }
        }
    }

    public static class IpcManager
    {
        public static string PipeName
        {
            get
            {
                var isIsolated = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIYUAN_URL"));
                return isIsolated ? "XiaoLvDesktop_IPC_Pipe_Isolated" : "XiaoLvDesktop_IPC_Pipe";
            }
        }

        /// <summary>
        /// 尝试向已在运行的实例发送指令，若发送成功则返回 true，说明已有实例在跑
        /// </summary>
        public static bool SendToRunningInstance(string message, int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = 1200;
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(timeoutMs);
                    using (var writer = new StreamWriter(client, Encoding.UTF8))
                    {
                        // 统一单行信封传输，避免多行截断
                        var singleLine = message.Replace("\r", " ").Replace("\n", "\\n");
                        writer.WriteLine(singleLine);
                        writer.Flush();
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SendToRunningInstance(string message)
        {
            return SendToRunningInstance(message, 1200);
        }

        /// <summary>
        /// 后台命名管道监听服务端
        /// </summary>
        public class Server : IDisposable
        {
            private bool _running = true;
            private Thread _listenThread;

            public event Action<string> OnMessageReceived;

            public void Start()
            {
                _running = true;
                _listenThread = new Thread(ListenLoop);
                _listenThread.IsBackground = true;
                _listenThread.Name = "XiaoLv_IpcServerThread";
                _listenThread.Start();
            }

            private void ListenLoop()
            {
                while (_running)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 10, PipeTransmissionMode.Byte))
                        {
                            pipe.WaitForConnection();
                            using (var reader = new StreamReader(pipe, Encoding.UTF8))
                            {
                                var line = reader.ReadLine();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    var msg = line.Trim();
                                    if (!msg.StartsWith("{") && msg.Contains("\\n"))
                                    {
                                        msg = msg.Replace("\\n", "\n");
                                    }
                                    if (OnMessageReceived != null)
                                    {
                                        OnMessageReceived(msg);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        if (!_running) break;
                        Thread.Sleep(80);
                    }
                }
            }

            public void Dispose()
            {
                _running = false;
                try
                {
                    if (_listenThread != null && _listenThread.IsAlive)
                    {
                        _listenThread.Abort();
                    }
                }
                catch { }
	            }
	        }
	    }
	}
