# 思源小驴 · Quicker 极简智能引信与终身免维护平稳过渡指南

> 💡 **核心战略**：  
> 将 Quicker 动作端以往庞大、易冲突、受制于 500KB 审核门禁的 363KB 复杂代码，**彻底解构降维为「零维护极简智能引信（Zero-Maintenance Fuse）」**。  
> **所有核心逻辑、UI 渲染、自动更新全面收敛回本地 3.87MB 原生客户端「思源外骨骼.exe」**。  
> Quicker 动作仅充当“0ms 快速唤醒开关”，老用户无需改变任何操作习惯，享受无感平稳过渡。

---

## 一、 老用户平稳过渡前后对比

| 体验维度 | 旧 Quicker 庞大动作（维护地狱） | 新「外骨骼 + Quicker 智能引信」（终身免维护） |
| :--- | :--- | :--- |
| **动作脚本体积** | 363 KB（逼近 Quicker 500KB 上限，需高压压缩剥离注释） | **< 2 KB（仅十余行代码，极其轻量稳固）** |
| **启动响应延迟** | 每次冷启动需等待 Roslyn 编译与加载（明显微顿） | **0ms 瞬发**（外骨骼底层常驻，物理管道毫秒呼出） |
| **后续升级维护** | 每次功能更新需重新打包过审，频遭机审误杀拦截 | **终身无需更新 Quicker 动作**；外骨骼内建国内高速自更新 |
| **Token 配置成本** | 需用户翻找设置手填 Webhook Token | **完全零 Token 配置**（走本机底层命名管道与私有协议） |
| **老用户肌肉记忆** | 习惯了点击 Quicker 面板格子或轮盘手势 | **100% 保持不变**，点击原位置直接唤出全新 3.0 大盘 |

---

## 二、 两个核心旗舰动作的极简引信实现

您可以在 Quicker 动作库中直接将现有的两大旗舰动作内容替换为以下极简脚本：

### 1. 旗舰动作一：【思源小驴·超级全局面板】(Alt+Shift+Q)

在 Quicker 中新建动作或编辑旧动作，添加一个**「运行脚本 (C#)」**步骤（或直接发 HTTP/启动进程），粘贴如下纯净代码：

```csharp
// 思源小驴 · 超级全局面板智能引信 (v3.0 终身免维护版)
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

public class ActionEntryPoint
{
    public static void Exec(Quicker.Public.IStepContext context)
    {
        string pipeName = "XiaoLvDesktop_IPC_Pipe";
        string command = "bento";

        // 1. 优先尝试命名管道 0ms 瞬间唤起已运行的外骨骼
        try
        {
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
            {
                client.Connect(150); // 150ms 快速探测
                using (var writer = new StreamWriter(client, Encoding.UTF8))
                {
                    writer.WriteLine(command);
                    writer.Flush();
                }
                return; // 唤醒成功，无感返回
            }
        }
        catch { }

        // 2. 管道未连接，探测本机外骨骼安装路径并启动
        string exePath = FindExoskeletonExe();
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = command,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            });
            return;
        }

        // 3. 本机未安装时，提供国内免翻一键极速下载安装
        var res = MessageBox.Show(
            "思源外骨骼已进化为 3.8MB 独立桌面工作台（0ms 响应、支持网页多段高亮剪藏与划词浮窗）。\n\n检测到本机尚未安装，是否立即极速下载启动？",
            "思源小驴 · 极速引信",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information
        );

        if (res == MessageBoxResult.Yes)
        {
            InstallExoskeletonFromCdn();
        }
    }

    private static string FindExoskeletonExe()
    {
        string[] candidates = new string[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoLvDesktop", "思源外骨骼.exe"),
            @"D:\思源插件\思源小驴桌面助手\bin\Release\思源外骨骼.exe",
            @"D:\思源插件\思源小驴桌面助手\思源外骨骼.exe"
        };
        foreach (var p in candidates) if (File.Exists(p)) return p;
        return null;
    }

    private static void InstallExoskeletonFromCdn()
    {
        string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoLvDesktop");
        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
        string targetExe = Path.Combine(targetDir, "思源外骨骼.exe");

        string downloadUrl = "https://ghfast.top/https://github.com/xiaolv-suite/siyuan-exoskeleton/releases/download/v3.0.0/siyuan-exoskeleton.exe";
        using (var wc = new System.Net.WebClient())
        {
            wc.Headers.Add("User-Agent", "Quicker-Fuse");
            wc.DownloadFile(downloadUrl, targetExe);
        }
        Process.Start(targetExe);
    }
}
```

---

### 2. 旗舰动作二：【思源小驴·随身划词笔】(Alt+Shift+W)

选中文本时，在 Quicker 动作中执行：

```csharp
// 思源小驴 · 随身划词笔极简引信 (带选中文本穿透)
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

public class ActionEntryPoint
{
    public static void Exec(Quicker.Public.IStepContext context)
    {
        // 获取当前 Quicker 捕获到的选中文本
        string selectedText = context.GetVarValue("selectedText") as string ?? "";
        string command = string.IsNullOrWhiteSpace(selectedText) ? "pen" : "pen:" + selectedText;

        try
        {
            using (var client = new NamedPipeClientStream(".", "XiaoLvDesktop_IPC_Pipe", PipeDirection.Out))
            {
                client.Connect(200);
                using (var writer = new StreamWriter(client, Encoding.UTF8))
                {
                    writer.WriteLine(command);
                    writer.Flush();
                }
                return;
            }
        }
        catch
        {
            // 兜底通过私有协议或进程唤起
            try { Process.Start("siyuan-exoskeleton://show?panel=capsule"); } catch { }
        }
    }
}
```

---

## 三、 老用户平稳迁移实施清单（给作者的操作指引）

1. **Quicker 动作集市策略**：
   - 将 Quicker 动作库中的 `L0-01` 与 `L1-00` 源码更新为上述「极简智能引信」；
   - 动作说明中注明：“🌟 本动作已升级为零延迟原生引信，搭配「思源外骨骼 v3.0」使用，享受 0ms 穿透与国内自动更新。”
2. **下线冗余子动作**：
   - 其余分散的 48 个细分动作无需再维护或发布，全部由外骨骼主面板内的 31 张场景卡片和进阶抽屉原生接管。
3. **老用户零感知享受红利**：
   - 老用户更新该 Quicker 动作后，若本机已在外骨骼运行态，点击瞬间无缝呼出；
   - 若未运行，引信自动极速唤起或国内 CDN 一键下载，平稳收拢全量用户进入独立客户端体系。
