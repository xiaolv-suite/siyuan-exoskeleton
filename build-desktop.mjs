import fs from 'fs';
import path from 'path';
import { execSync } from 'child_process';

const ROOT = 'D:/思源插件/思源小驴桌面助手';
const CSC = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe';
const WPF_DIR = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF';
const NET_DIR = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319';

const outDir = path.join(ROOT, 'bin/Release');
fs.mkdirSync(outDir, { recursive: true });

// 1. Gracefully terminate running instances if any & sweep ghost tray icons
try {
  execSync('chcp 65001 > nul && taskkill /F /T /IM "思源外骨骼.exe" & taskkill /F /T /IM "思源小驴.exe"', { stdio: "ignore" });
  // 扫清任务栏与溢出托盘中的幽灵图标
  const sweepScript = `
    Add-Type -TypeDefinition @'
    using System; using System.Runtime.InteropServices;
    public class TC {
      [DllImport(\"user32.dll\")] public static extern IntPtr FindWindow(string c, string w);
      [DllImport(\"user32.dll\")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
      [DllImport(\"user32.dll\")] public static extern bool GetClientRect(IntPtr h, out RECT r);
      [DllImport(\"user32.dll\")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
      [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
      public static void S() {
        IntPtr t = FindWindow(\"Shell_TrayWnd\", null);
        if (t != IntPtr.Zero) {
          IntPtr n = FindWindowEx(t, IntPtr.Zero, \"TrayNotifyWnd\", null);
          if (n != IntPtr.Zero) {
            IntPtr p = FindWindowEx(n, IntPtr.Zero, \"SysPager\", null);
            IntPtr b = FindWindowEx(p != IntPtr.Zero ? p : n, IntPtr.Zero, \"ToolbarWindow32\", null);
            if (b != IntPtr.Zero) W(b);
          }
        }
        IntPtr o = FindWindow(\"NotifyIconOverflowWindow\", null);
        if (o != IntPtr.Zero) {
          IntPtr b = FindWindowEx(o, IntPtr.Zero, \"ToolbarWindow32\", null);
          if (b != IntPtr.Zero) W(b);
        }
      }
      private static void W(IntPtr b) {
        RECT r; if (GetClientRect(b, out r)) {
          for (int x = 2; x < r.R; x += 6) {
            for (int y = 2; y < r.B; y += 6) {
              SendMessage(b, 0x0200, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
            }
          }
        }
      }
    }
'@ -ErrorAction SilentlyContinue; [TC]::S();
  `;
  execSync(`powershell.exe -NoProfile -Command "${sweepScript.replace(/\r?\n/g, ' ')}"`, { stdio: "ignore" });
} catch {}

// 2. Sync WebView2 loose dependencies for directory mode
const libDir = path.join(ROOT, 'lib');
fs.readdirSync(libDir).forEach(f => {
  fs.copyFileSync(path.join(libDir, f), path.join(outDir, f));
});

// 3. Sync loose assets folder for rapid live reload mode
const assetsDst = path.join(outDir, 'assets');
fs.mkdirSync(assetsDst, { recursive: true });
const assetsSrc = path.join(ROOT, 'assets');
fs.cpSync(assetsSrc, assetsDst, { recursive: true });

// 4. Compress assets into assets.zip for embedded single-file self-extract mode
const assetsZip = path.join(ROOT, 'assets.zip');
console.log('Packing assets.zip for single-file embedding...');
execSync(`powershell -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; if(Test-Path '${assetsZip}') { Remove-Item '${assetsZip}' -Force }; [System.IO.Compression.ZipFile]::CreateFromDirectory('${assetsSrc}', '${assetsZip}')"`);

const targetExe = path.join(outDir, '思源外骨骼.exe');

// 递归扫描 src 下全部源码文件
function getAllSourceFiles(dir) {
  let results = [];
  const list = fs.readdirSync(dir);
  list.forEach(file => {
    const fullPath = path.join(dir, file);
    const stat = fs.statSync(fullPath);
    if (stat && stat.isDirectory()) {
      results = results.concat(getAllSourceFiles(fullPath));
    } else if (file.endsWith('.cs')) {
      results.push(fullPath);
    }
  });
  return results;
}

const sourceFiles = getAllSourceFiles(path.join(ROOT, 'src'));
console.log(`Discovered ${sourceFiles.length} C# source files under src/`);

const rsp = [
  '/target:winexe',
  `/out:${targetExe}`,
  '/platform:x64',
  '/optimize+',
  '/warn:4',
  '/reference:System.dll',
  '/reference:System.Core.dll',
  '/reference:System.Data.dll',
  '/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll',
  '/reference:Microsoft.CSharp.dll',
  `/reference:${path.join(NET_DIR, 'System.Runtime.dll')}`,
  `/reference:${path.join(NET_DIR, 'System.Runtime.WindowsRuntime.dll')}`,
  `/reference:${path.join(NET_DIR, 'System.IO.Compression.dll')}`,
  `/reference:${path.join(NET_DIR, 'System.IO.Compression.FileSystem.dll')}`,
  `/reference:${path.join(NET_DIR, 'System.Web.Extensions.dll')}`,
  `/reference:${path.join(NET_DIR, 'System.Net.Http.dll')}`,
  `/reference:${path.join(WPF_DIR, 'WindowsBase.dll')}`,
  `/reference:${path.join(WPF_DIR, 'PresentationCore.dll')}`,
  `/reference:${path.join(WPF_DIR, 'PresentationFramework.dll')}`,
  `/reference:${path.join(WPF_DIR, 'UIAutomationClient.dll')}`,
  `/reference:${path.join(WPF_DIR, 'UIAutomationTypes.dll')}`,
  `/reference:${path.join(NET_DIR, 'System.Xaml.dll')}`,
  `/reference:${path.join(outDir, 'Microsoft.Web.WebView2.Core.dll')}`,
  `/reference:${path.join(outDir, 'Microsoft.Web.WebView2.Wpf.dll')}`,
  // Embedded Resources for zero-dependency standalone portability
  `/resource:${path.join(libDir, 'Microsoft.Web.WebView2.Core.dll')},Microsoft.Web.WebView2.Core.dll`,
  `/resource:${path.join(libDir, 'Microsoft.Web.WebView2.Wpf.dll')},Microsoft.Web.WebView2.Wpf.dll`,
  `/resource:${path.join(libDir, 'WebView2Loader.dll')},WebView2Loader.dll`,
  `/resource:${assetsZip},assets.zip`,
  ...sourceFiles
].join('\n');

const rspPath = path.join(ROOT, 'build.rsp');
fs.writeFileSync(rspPath, rsp, 'utf8');

console.log('Compiling complete XiaoLv Desktop standalone application with full resource embedding...');
try {
  const out = execSync(`"${CSC}" @"${rspPath}"`).toString();
  console.log(out);
  console.log('🎉 COMPILED SUCCESSFULLY!');
  fs.unlinkSync(rspPath);

  const stat = fs.statSync(targetExe);
  console.log(`Executable size: ${stat.size} bytes (${(stat.size / 1024 / 1024).toFixed(2)} MB)`);
  // 兼容别名拷贝，确保旧快捷方式与 Quicker 动作无缝衔接
  fs.copyFileSync(targetExe, path.join(outDir, '思源小驴.exe'));
} catch (err) {
  console.error('Build failed:\n', err.stdout ? err.stdout.toString() : err.message);
  process.exit(1);
}
