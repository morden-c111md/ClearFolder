using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClearFolder {
    internal static class TrayDiagnostics {
        internal static bool Run(string root) {
            string directory=Path.Combine(root,"artifacts","test-tray-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(directory);var report=new StringBuilder();bool passed=false;TrayController controller=null;
            try {
                var store=new Store(Path.Combine(directory,"store"));store.Load();Deck deck=store.State.Decks[0];
                var item=new Entry {Managed=true,Name="Synthetic owned project",OriginalPath=Path.Combine(directory,"original.txt")};
                item.Path=Path.Combine(store.Root,"files",deck.Id,item.Id,"synthetic.txt");Directory.CreateDirectory(Path.GetDirectoryName(item.Path));File.WriteAllText(item.Path,"synthetic payload");
                deck.Entries.Add(item);store.Save();controller=new TrayController(store,root);
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var menu=(ContextMenuStrip)typeof(TrayController).GetField("menu",flags).GetValue(controller);
                var exit=(ToolStripMenuItem)typeof(TrayController).GetField("exitItem",flags).GetValue(controller);
                var tray=(NotifyIcon)typeof(TrayController).GetField("tray",flags).GetValue(controller);
                bool exited=false;controller.ThreadExit+=delegate{exited=true;};
                Point location=new Point(Screen.PrimaryScreen.WorkingArea.Right-400,Screen.PrimaryScreen.WorkingArea.Top+200);
                menu.Show(location);Pump(100);
                bool locked=!exit.Enabled&&exit.Text.Contains("退出已锁定");exit.PerformClick();Pump(50);
                bool stayed=!exited&&tray.Visible;menu.Close();
                report.AppendLine("Actual tray menu locked with managed payload: "+locked);report.AppendLine("Disabled exit click leaves application and tray running: "+stayed);
                // Release only synthetic metadata; retain the test payload on disk for inspection.
                deck.Entries.Clear();store.Save();menu.Show(location);Pump(100);
                bool unlocked=exit.Enabled&&exit.Text=="退出 ClearFolder";menu.Close();exit.PerformClick();Pump(50);
                report.AppendLine("Actual tray exit unlocks after final managed entry is removed: "+unlocked);
                report.AppendLine("Unlocked exit disposes tray and exits context: "+exited);
                using(var preview=TrayIcon.Render(128))preview.Save(Path.Combine(root,"artifacts","tray-icon-preview.png"),ImageFormat.Png);
                using(var icon=TrayIcon.Create())report.AppendLine("Custom tray icon dimensions: "+icon.Size);
                passed=locked&&stayed&&unlocked&&exited;
            }catch(Exception error){report.AppendLine(error.ToString());}
            finally{if(controller!=null)controller.Dispose();}
            report.AppendLine("Result: "+(passed?"PASS":"FAIL"));report.AppendLine("Synthetic data retained: "+directory);
            File.WriteAllText(Path.Combine(root,"artifacts","tray-test.txt"),report.ToString());return passed;
        }
        static void Pump(int milliseconds){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(8);}}
    }
}
