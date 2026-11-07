using System;
using System.IO;
using Microsoft.Win32;

namespace XiaoLv.Desktop.Core
{
    public static class ShellContextMenuManager
    {
        private const string FILE_KEY = @"Software\Classes\*\shell\XiaoLvSendToSiYuan";
        private const string DIR_KEY = @"Software\Classes\Directory\shell\XiaoLvSendToSiYuan";
        private const string MENU_TEXT_FILE = "小驴速递 · 归档入思源";
        private const string MENU_TEXT_DIR = "小驴速递 · 归档目录入思源";

        public static bool IsRegistered()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(FILE_KEY))
                {
                    if (k == null) return false;
                    using (var cmdKey = k.OpenSubKey("command"))
                    {
                        if (cmdKey == null) return false;
                        var val = cmdKey.GetValue("") as string;
                        var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                        return val != null && val.Contains(currentExe);
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Register()
        {
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

                // 1. 注册单文件右键菜单
                using (var k = Registry.CurrentUser.CreateSubKey(FILE_KEY))
                {
                    if (k != null)
                    {
                        k.SetValue("", MENU_TEXT_FILE);
                        k.SetValue("Icon", "\"" + exePath + "\",0");
                        using (var cmd = k.CreateSubKey("command"))
                        {
                            if (cmd != null)
                            {
                                cmd.SetValue("", "\"" + exePath + "\" -sendfile \"%1\"");
                            }
                        }
                    }
                }

                // 2. 注册文件夹右键菜单
                using (var k = Registry.CurrentUser.CreateSubKey(DIR_KEY))
                {
                    if (k != null)
                    {
                        k.SetValue("", MENU_TEXT_DIR);
                        k.SetValue("Icon", "\"" + exePath + "\",0");
                        using (var cmd = k.CreateSubKey("command"))
                        {
                            if (cmd != null)
                            {
                                cmd.SetValue("", "\"" + exePath + "\" -sendfile \"%1\"");
                            }
                        }
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Unregister()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(FILE_KEY, false);
                Registry.CurrentUser.DeleteSubKeyTree(DIR_KEY, false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
