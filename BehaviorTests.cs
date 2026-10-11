using System;
using System.Drawing;
using System.IO;
using System.Linq;

namespace ClearFolder.Tests {
    internal static class BehaviorTests {
        static int checks;
        static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
        static void Throws(Action action,string name){bool threw=false;try{action();}catch{threw=true;}Check(threw,name);}
        [STAThread] static int Main(string[] args) {
            try {
                string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
                Check(Layout.ColumnCount(0)==2&&Layout.ColumnCount(4)==2,"2-column grid for <=4 items");
                checks+=ShellBehaviorTests.Run(Path.Combine(root,"shell"));
                checks+=TransferBehaviorTests.Run(Path.Combine(root,"transfers"));
                checks+=DropDestinationTests.Run(Path.Combine(root,"destinations"));
                var ripples=new RippleState();ripples.Pointer(20,20,0,true,true);var firstWave=ripples.Waves[0];
                ripples.Pointer(55,20,.1,true,true);
                Check(ripples.Waves.Count==2&&firstWave.Born==0&&firstWave.X==20,"pointer motion preserves older wave birth and origin");
                ripples.Pointer(80,20,.12,true,true);Check(ripples.Waves.Count==2,"ripple emission is throttled");
                ripples.Pointer(90,20,.2,true,true);ripples.Pointer(120,20,.3,true,true);Check(ripples.Waves.Count==3,"ripple history bounded to three rings");
                ripples.Advance(2);Check(ripples.Waves.Count==0,"ripples expire independently");
                ripples.Pointer(20,20,3,true,false);Check(ripples.Waves.Count==0,"disabled ripples emit no rings");
                Check(StartupRegistration.Command(Path.Combine(root,"space directory","ClearFolder.exe")).EndsWith("ClearFolder.exe\" --login"),"login command quotes paths containing spaces");
                var exitSettings=new Settings();var exitDeck=new Deck();exitSettings.Decks.Add(exitDeck);
                Check(ExitProtection.Reason(exitSettings,false,false)==null,"empty folders allow normal tray exit");
                exitDeck.Entries.Add(new Entry {Managed=false});
                Check(ExitProtection.Reason(exitSettings,false,false)==null,"path references alone do not lock exit");
                exitDeck.Entries.Add(new Entry {Managed=true});
                Check(ExitProtection.Reason(exitSettings,false,false)!=null,"managed collected project locks normal exit");
                exitDeck.Entries.Clear();
                Check(ExitProtection.Reason(exitSettings,true,false)!=null&&ExitProtection.Reason(exitSettings,false,true)!=null,"active and pending transfers both lock exit");
                Check(ExitProtection.Reason(exitSettings,false,false)==null,"releasing final managed item restores normal exit");
                Check(Layout.ColumnCount(5)==3&&Layout.ColumnCount(9)==3,"3-column grid for 5..9 items");
                Check(Layout.ColumnCount(10)==4&&Layout.ColumnCount(20)==4,"4-column grid for >=10 items");
                var work=new Rectangle(0,0,1920,1040);
                var desktopLayout=Layout.Calculate(new Point(136,149),4,0,work,1.5f,new Size(115,147),72);
                Check(desktopLayout.Icon.Size==new Size(86,86)&&desktopLayout.Bounds.X+desktopLayout.Icon.X+43==172&&desktopLayout.Bounds.Y+desktopLayout.Icon.Y+43==185,"collapsed folder grows twenty percent around native icon center");
                var adjacent=Layout.Calculate(new Point(251,149),4,0,work,1.5f,new Size(115,147),72);
                Check(desktopLayout.Icon.Width<115&&desktopLayout.Bounds.X+desktopLayout.Icon.Right<adjacent.Bounds.X+adjacent.Icon.Left,"enlarged folder remains within one desktop grid cell");
                var corner=Layout.Calculate(new Point(0,2),4,0,work,1.5f,new Size(115,147),72);
                Check(corner.Bounds.X+corner.Icon.X>=work.Left&&corner.Bounds.Y+corner.Icon.Y>=work.Top,"enlarged edge anchor keeps visible glass inside work area");
                var normal=Layout.Calculate(new Point(200,100),6,0,work,1);
                Check(normal.Cells.Length==6&&normal.Cells[0].Y==normal.Cells[2].Y&&normal.Cells[3].Y>normal.Cells[2].Y,"six icons form two rows of three");
                Check(!normal.Left&&normal.Bounds.Right<=work.Right,"popup expands right in available space");
                var edge=Layout.Calculate(new Point(1850,990),16,0,work,1);
                Check(edge.Left&&edge.Bounds.Right<=work.Right&&edge.Bounds.Bottom<=work.Bottom,"edge popup flips left and stays in work area");
                var small=Layout.Calculate(new Point(1200,660),20,0,new Rectangle(0,0,1366,720),2);
                Check(small.Bounds.Left>=0&&small.Bounds.Top>=0&&small.Bounds.Right<=1366&&small.Bounds.Bottom<=720,"high-DPI multipage grid fits small work area");
                var negative=Layout.Calculate(new Point(-1800,100),9,0,new Rectangle(-1920,0,1920,1040),1.5f);
                Check(negative.Bounds.Left>=-1920&&negative.Bounds.Right<=0,"negative-coordinate monitor layout");
                var paged=Layout.Calculate(new Point(200,100),19,1,work,1);
                Check(paged.Page==1&&paged.Pages==2&&paged.Start==16&&paged.Cells.Length==3,"paging preserves item indices");
                using(var path=normal.PanelPath())Check(path.IsVisible(normal.Panel.X+20,normal.Panel.Y+20),"panel shape contains interior");
                foreach(var route in new[]{normal,edge,desktopLayout}) {
                    int from=route.Left?route.Icon.Left:route.Icon.Right,to=route.Left?route.Panel.Right:route.Panel.Left;
                    var gap=new Point((from+to)/2,route.Icon.Y+route.Icon.Height/2+(int)(18*route.Scale));
                    Check(route.ContainsHover(gap,true)&&!route.Contains(gap,true),"wide hover corridor keeps off-triangle transit without intercepting clicks");
                    Check(!route.ContainsHover(gap,false),"collapsed folder does not acquire hidden hover corridor");
                    Check(!route.ContainsHover(new Point(gap.X,Math.Max(route.Icon.Bottom,route.Panel.Bottom)+50),true),"leaving corridor remains outside hover region");
                    Check(!route.InHoverCorridor(new Point(gap.X,route.Bounds.Height)),"hover corridor never includes window bottom exclusive boundary");
                }
                foreach(var route in new[]{Layout.Calculate(new Point(136,149),16,0,work,1.5f,new Size(115,147),72),Layout.Calculate(new Point(1800,149),16,0,work,1.5f,new Size(115,147),72)}) {
                    Point start=new Point(route.Icon.X+route.Icon.Width/2,route.Icon.Y+route.Icon.Height/2);
                    Rectangle targetCell=route.Cells[route.Left?15:12];Point target=new Point(targetCell.X+targetCell.Width/2,targetCell.Y+targetCell.Height/2);
                    bool continuous=true;
                    for(int step=0;step<=100;step++)continuous&=route.ContainsHover(new Point(start.X+(target.X-start.X)*step/100,start.Y+(target.Y-start.Y)*step/100),true);
                    Check(continuous,"four-row near-side bottom icon has uninterrupted diagonal hover route");
                }
                var hover=new HoverState();
                hover.Update(0,true,false);hover.Update(219,true,false);Check(!hover.Open,"hover waits 220ms");
                hover.Update(220,true,false);Check(hover.Open,"hover opens at 220ms");
                hover.Update(300,false,false);Check(!hover.Open,"leaving closes immediately on the first update");
                hover.Update(301,true,false);Check(!hover.Open,"re-entry still waits for hover opening");
                hover.Open=true;hover.Update(1000,false,false);hover.Update(1200,true,false);hover.Update(2000,true,false);Check(hover.Open,"re-entry can reopen after normal hover threshold");
                hover.Update(3000,false,true);hover.Update(10000,false,true);Check(hover.Open,"menus and drag block close");
                var store=new Store(Path.Combine(root,"normal"));store.Load();Deck deck=store.State.Decks[0];
                string file=Path.Combine(root,"source.txt");File.WriteAllText(file,"original-content");
                Entry item=store.AddReference(deck,file);store.AddReference(deck,file);
                Check(deck.Entries.Count==1&&File.ReadAllText(file)=="original-content","adding reference deduplicates without moving source");
                var reloaded=new Store(store.Root);reloaded.Load();Check(reloaded.State.Decks[0].Entries[0].Path==file,"layout persists and reloads");
                store.RemoveReference(deck,item);Check(File.Exists(file)&&deck.Entries.Count==0,"removing reference preserves file");
                item=store.AddReference(deck,file);Throws(delegate{store.MoveIntoStorage(deck,item);},"non-desktop move rejected");
                string dir=Path.Combine(store.Root,"files",deck.Id,item.Id);Directory.CreateDirectory(dir);
                string managed=Path.Combine(dir,"owned.txt");File.WriteAllText(managed,"owned-content");
                item.Path=managed;item.Managed=true;item.OriginalPath=Path.Combine(root,"restored.txt");store.Save();
                File.WriteAllText(item.OriginalPath,"collision");
                Throws(delegate{store.Restore(deck,item);},"restore refuses overwrite");
                Check(File.ReadAllText(managed)=="owned-content"&&File.ReadAllText(item.OriginalPath)=="collision","restore conflict keeps both files intact");
                item.OriginalPath=Path.Combine(root,"restored-unique.txt");store.Restore(deck,item);
                Check(!item.Managed&&File.ReadAllText(item.Path)=="owned-content"&&!File.Exists(managed),"restore updates ownership and physical file");
                string corruptRoot=Path.Combine(root,"corrupt");Directory.CreateDirectory(corruptRoot);string corrupt=Path.Combine(corruptRoot,"layout.json");File.WriteAllText(corrupt,"{broken");
                Throws(delegate{new Store(corruptRoot).Load();},"corrupt layout rejected");Check(File.ReadAllText(corrupt)=="{broken","corrupt layout not overwritten");
                var recovery=new Store(Path.Combine(root,"recovery"));recovery.Load();var recoveryDeck=recovery.State.Decks[0];
                string original=Path.Combine(root,"journal-original.txt");File.WriteAllText(original,"journal-data");var journalItem=recovery.AddReference(recoveryDeck,original);
                string destination=Path.Combine(recovery.Root,"files",recoveryDeck.Id,journalItem.Id,"journal.txt");Directory.CreateDirectory(Path.GetDirectoryName(destination));
                Store.WriteAtomic(Path.Combine(recovery.Root,"pending-move.json"),new MoveJournal{Source=original,Destination=destination,DeckId=recoveryDeck.Id,Item=journalItem});File.Move(original,destination);
                var recovered=new Store(recovery.Root);recovered.Load();var recoveredItem=recovered.State.Decks[0].Entries[0];
                Check(recoveredItem.Managed&&recoveredItem.Path==destination&&File.ReadAllText(destination)=="journal-data","startup recovers completed move before layout commit");
                using(var source=new Bitmap(normal.Bounds.Width,normal.Bounds.Height))using(var g=Graphics.FromImage(source))using(var renderer=new GlassRenderer()) {
                    g.Clear(Color.FromArgb(60,110,170));renderer.SetBackground(source,1.12);
                    var renderDeck=new Deck{Name="Test"};
                    for(int i=0;i<6;i++)renderDeck.Entries.Add(new Entry{Path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),Name="Test"});
                    using(var frame=renderer.Render(normal,renderDeck,new Settings(),1,new PointF(-100,-100),0,4,-1,false)) {
                        Color sample=frame.GetPixel(normal.Panel.X+normal.Panel.Width-20,normal.Panel.Y+40);
                        Check(sample.A==255&&sample.R<60&&sample.B>170,"glass preserves background contrast without white haze");
                        Color collapsed=frame.GetPixel(normal.Icon.X+4,normal.Icon.Y+normal.Icon.Height/2);
                        Check(Math.Abs(collapsed.R-sample.R)<=1&&Math.Abs(collapsed.B-sample.B)<=1,"collapsed and expanded surfaces share material");
                    }
                }
                Console.WriteLine("ALL PASS: "+checks+" checks");return 0;
            }catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
        }
    }
}



