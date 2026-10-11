using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ClearFolder {
    internal static class Program {
        [STAThread] static int Main(string[] args) {
            Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
            try {
                if(args.Contains("--render-preview")){Diagnostics.RenderPreview(root);return 0;}
                if(args.Contains("--capture-test")){return Diagnostics.CaptureProbe(root)?0:2;}
                if(args.Contains("--smoke")){Diagnostics.Smoke(root);return 0;}
                if(args.Contains("--interaction-test")){return Diagnostics.InteractionTest(root)?0:2;}
                if(args.Contains("--drop-test")){return Diagnostics.DropTest(root)?0:2;}
                if(args.Contains("--transfer-test")){return TransferDiagnostics.Run(root)?0:2;}
                if(args.Contains("--usability-test")){return UsabilityDiagnostics.Run(root)?0:2;}
                if(args.Contains("--icon-test")){return IconDiagnostics.Run(root)?0:2;}
                if(args.Contains("--tray-test")){return TrayDiagnostics.Run(root)?0:2;}
                if(args.Contains("--dynamic-background-test")){return DynamicBackgroundDiagnostics.Run(root)?0:2;}
                if(args.Contains("--ripple-menu-test")){return RippleMenuDiagnostics.Run(root)?0:2;}
                if(args.Contains("--animation-test")){return RevisionDiagnostics.Run(root)?0:2;}
                bool created;
                using(var mutex=new Mutex(true,"Local\\ClearFolder.Desktop",out created)) {
                    if(!created){MessageBox.Show("ClearFolder 已在运行，请从任务栏通知区域的图标打开菜单。","ClearFolder");return 0;}
                    var store=new Store(Path.Combine(root,"data"));store.Load();
                    if(args.Contains("--enable-ripples")){store.State.RippleStrength=1.8;store.Save();}
                    Application.Run(new TrayController(store,root));
                }
                return 0;
            }catch(Exception error) {
                string message=error.GetType().Name+": "+error.Message;
                if(args.Length>0) {
                    try {
                        Directory.CreateDirectory(Path.Combine(root,"artifacts"));
                        File.WriteAllText(Path.Combine(root,"artifacts","diagnostic-error.txt"),error.ToString());
                    }catch(Exception logError){message+="\n诊断日志也未能写入："+logError.Message;}
                }
                if(args.Length==0||args.Contains("--login")||args.Contains("--enable-ripples"))MessageBox.Show("启动失败，原布局和文件已保留。\n\n"+message,"ClearFolder",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
    }
    internal sealed class TrayController : ApplicationContext {
        readonly Store store;
        readonly string root;
        readonly List<FolderWindow> windows=new List<FolderWindow>();
        readonly NotifyIcon tray;
        readonly Icon trayArtwork;
        readonly ContextMenuStrip menu=new ContextMenuStrip();
        readonly ToolStripMenuItem exitItem;
        readonly ToolStripMenuItem retryTransferItem;
        bool force;
        public TrayController(Store state,string project) {
            store=state;root=project;
            foreach(var deck in store.State.Decks)windows.Add(new FolderWindow(store,deck));
            try{store.Save();}catch(Exception error){MessageBox.Show("网格位置暂未保存，下次启动仍会重新吸附。\n"+error.Message,"布局保存失败");}
            menu.Items.Add("定位所有文件夹（短暂显示在应用上方）",null,delegate{foreach(var w in windows)w.Locate();});
            menu.Items.Add("新建收纳文件夹…",null,delegate{NewDeck();});
            menu.Items.Add("添加文件引用到第一个文件夹…",null,delegate{windows[0].AddFiles();});
            var forceItem=new ToolStripMenuItem("临时置于应用上方（调试/试用）");
            forceItem.Click+=delegate{force=!force;forceItem.Checked=force;foreach(var w in windows){w.ForceVisible=force;w.Reveal();}};
            menu.Items.Add(forceItem);menu.Items.Add(new ToolStripSeparator());
            string executable=Path.Combine(root,"artifacts","ClearFolder.exe");
            var startupItem=new ToolStripMenuItem("登录 Windows 后自动恢复收纳文件夹");
            startupItem.Checked=StartupRegistration.Enabled(executable);
            startupItem.Click+=delegate {
                bool enable=!startupItem.Checked;
                if(MessageBox.Show(enable?"将为当前用户添加 ClearFolder 登录启动项，重启并登录后恢复现有布局。\n不会移动桌面文件。是否开启？":"关闭 ClearFolder 的当前用户登录启动项？","登录启动",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                try{StartupRegistration.SetEnabled(executable,enable);startupItem.Checked=StartupRegistration.Enabled(executable);}
                catch(Exception error){MessageBox.Show(error.Message,"登录启动设置失败");}
            };
            menu.Items.Add(startupItem);
            menu.Items.Add("查看渲染状态",null,delegate{MessageBox.Show(windows.Count>0?windows[0].RendererDescription+"\n"+windows[0].GridDescription:"没有收纳文件夹","ClearFolder 渲染状态");});
            menu.Items.Add("打开布局与收纳目录",null,delegate{Process.Start(new ProcessStartInfo(store.Root){UseShellExecute=true});});
            retryTransferItem=new ToolStripMenuItem("重试完成移动记录");
            retryTransferItem.Click+=delegate {
                if(store.IsTransferActive||windows.Any(w=>w.IsTransferBusy)){MessageBox.Show("文件移动仍在完成，请稍后再试。","ClearFolder");return;}
                try{store.RetryPendingTransfer();foreach(var window in windows)window.Recalculate();UpdateExitProtection();MessageBox.Show("移动记录已处理，可以继续收纳或拖出项目。","ClearFolder");}
                catch(Exception error){MessageBox.Show("移动记录仍保留，未覆盖或删除文件。\n"+error.Message,"移动恢复未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            };
            menu.Items.Add(retryTransferItem);
            menu.Items.Add("使用说明",null,delegate{Process.Start(new ProcessStartInfo(Path.Combine(root,"README.md")){UseShellExecute=true});});
            menu.Items.Add(new ToolStripSeparator());
            exitItem=new ToolStripMenuItem("退出 ClearFolder");
            exitItem.Click+=delegate{ExitThread();};menu.Items.Add(exitItem);
            menu.Opening+=delegate{UpdateExitProtection();};UpdateExitProtection();
            trayArtwork=TrayIcon.Create();
            tray=new NotifyIcon {Icon=trayArtwork,Text="ClearFolder · 悬停收纳",ContextMenuStrip=menu,Visible=true};
            tray.DoubleClick+=delegate{foreach(var w in windows)w.Locate();};
            tray.ShowBalloonTip(6000,"ClearFolder 已启动","按 Win+D 查看桌面；悬停文件夹展开，右键添加项目。拖入会移动原文件；拖出可移回桌面或文件夹。",ToolTipIcon.Info);
        }
        void NewDeck() {
            if(store.HasPendingTransfer){MessageBox.Show("文件移动尚未完成，请稍后再新建。");return;}
            string name=InputDialog.Ask("新建收纳文件夹","新文件夹");if(name==null)return;
            if(windows.Count>=30){MessageBox.Show("当前版本最多支持30个收纳文件夹。");return;}
            var work=Screen.FromPoint(Cursor.Position).WorkingArea;
            var deck=new Deck {Name=name,X=work.Left+220+(windows.Count%6)*90,Y=work.Top+100+(windows.Count/6)*100};
            store.State.Decks.Add(deck);
            try{store.Save();windows.Add(new FolderWindow(store,deck){ForceVisible=force});}
            catch(Exception error){store.State.Decks.Remove(deck);MessageBox.Show(error.Message,"创建失败");}
        }
        protected override void ExitThreadCore() {
            string reason=ExitProtection.Reason(store.State,windows.Any(w=>w.IsTransferBusy),store.HasPendingTransfer);
            if(reason!=null){MessageBox.Show("无法退出："+reason+"。\n请先将已收纳项目拖出或恢复；可从托盘打开收纳目录。","ClearFolder 退出保护");return;}
            tray.Visible=false;tray.Dispose();trayArtwork.Dispose();menu.Dispose();foreach(var window in windows)window.Dispose();base.ExitThreadCore();
        }
        void UpdateExitProtection() {
            string reason=ExitProtection.Reason(store.State,windows.Any(w=>w.IsTransferBusy),store.HasPendingTransfer);
            exitItem.Enabled=reason==null;
            exitItem.Text=reason==null?"退出 ClearFolder":"退出已锁定（"+reason+"）";
            retryTransferItem.Enabled=store.HasPendingTransfer&&!store.IsTransferActive&&!windows.Any(w=>w.IsTransferBusy);
        }
    }
}


