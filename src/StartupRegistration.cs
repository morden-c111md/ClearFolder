using System;
using System.IO;
using Microsoft.Win32;

namespace ClearFolder {
    internal static class StartupRegistration {
        const string KeyPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName="ClearFolder";
        internal static string Command(string executable){return "\""+Path.GetFullPath(executable)+"\" --login";}
        internal static bool Enabled(string executable) {
            using(var key=Registry.CurrentUser.OpenSubKey(KeyPath,false))
                return key!=null&&string.Equals(key.GetValue(ValueName) as string,Command(executable),StringComparison.OrdinalIgnoreCase);
        }
        // Called only after the user confirms the corresponding menu action.
        internal static void SetEnabled(string executable,bool enabled) {
            using(var key=Registry.CurrentUser.CreateSubKey(KeyPath)) {
                if(enabled)key.SetValue(ValueName,Command(executable),RegistryValueKind.String);
                else if(string.Equals(key.GetValue(ValueName) as string,Command(executable),StringComparison.OrdinalIgnoreCase))key.DeleteValue(ValueName,false);
            }
        }
    }
}

