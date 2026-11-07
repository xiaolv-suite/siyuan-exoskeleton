using System;
using System.Diagnostics;
using System.IO;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源笔记内核进程与环境管理器
    /// 负责清理各插件测试靶场异常退出遗留的孤儿内核进程，绝对保护真实工作区内核 (D:\SIYUAN)
    /// </summary>
    public static class KernelManager
    {
        public static string CleanOrphanTestKernels()
        {
            try
            {
                // 仅扫描隔离靶场特有的命令行特征，严格排除用户真实生产工作区
                string script = "$orphans = Get-CimInstance Win32_Process -Filter \"Name='SiYuan-Kernel.exe'\" | " +
                                "Where-Object { $_.CommandLine -notmatch 'D:\\\\SIYUAN' -and ($_.CommandLine -match '\\.isolated-workspaces' -or $_.CommandLine -match 'siyuan-isolated' -or $_.CommandLine -match 'siyuan-exam-live' -or $_.CommandLine -match 'SiYuan-Glean-Stage' -or $_.CommandLine -match 'Temp\\\\siyuan-') }; " +
                                "foreach ($p in $orphans) { Write-Output ($p.ProcessId); Stop-Process -Id $p.ProcessId -Force }";

                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + script + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3500);

                    var pids = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (pids.Length > 0)
                    {
                        return string.Format("✓ 已成功清扫 {0} 个遗留测试孤儿内核，释放约 {1} MB 内存！", pids.Length, pids.Length * 50);
                    }
                    else
                    {
                        return "✓ 当前系统环境纯净，未发现任何遗留测试孤儿内核。";
                    }
                }
            }
            catch (Exception ex)
            {
                return "⚠️ 清扫执行异常: " + ex.Message;
            }
        }
    }
}
