using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClearFolder.Tests {
    internal static class TransferBehaviorTests {
        static int checks;
        static void Check(bool okay,string name){if(!okay)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
        static void Throws(Action action,string name){bool threw=false;try{action();}catch{threw=true;}Check(threw,name);}
        public static int Run(string root) {
            checks=0;Directory.CreateDirectory(root);string desktop=Path.Combine(root,"Desktop"),common=Path.Combine(root,"PublicDesktop"),destination=Path.Combine(root,"Destination");
            Directory.CreateDirectory(desktop);Directory.CreateDirectory(common);Directory.CreateDirectory(destination);
            var store=new Store(Path.Combine(root,"data"),desktop,common);store.Load();Deck deck=store.State.Decks[0];
            Check(deck.Entries.Count==0,"new layout has no prefilled example entries");
            string file=Path.Combine(desktop,"roundtrip.txt");File.WriteAllText(file,"roundtrip-content");
            var priorContext=SynchronizationContext.Current;var context=new QueuedContext();Task<Entry> collect;
            try {
                SynchronizationContext.SetSynchronizationContext(context);collect=store.CollectAsync(deck,file);
                Check(store.HasPendingTransfer,"async transfer exposes pending state before UI commit");
                Throws(delegate{store.RetryPendingTransfer();},"journal retry cannot race active filesystem or UI commit work");
                Throws(delegate{store.AddReference(deck,file);},"pending transfer blocks adding references");
                Throws(delegate{store.RemoveReference(deck,deck.Entries[0]);},"pending transfer blocks removing entry");
                Throws(delegate{store.CollectAsync(deck,file).GetAwaiter().GetResult();},"pending transfer blocks a concurrent collect");
                var wait=Stopwatch.StartNew();while(File.Exists(file)&&wait.ElapsedMilliseconds<3000)Thread.Sleep(2);
                Check(!File.Exists(file)&&!deck.Entries[0].Managed,"filesystem work completes before queued UI metadata commit");
                context.Complete(collect);Entry item=collect.GetAwaiter().GetResult();
                Check(item.Managed&&File.ReadAllText(item.Path)=="roundtrip-content"&&!store.HasPendingTransfer,"collect moves original into storage and commits managed entry");
            }finally{SynchronizationContext.SetSynchronizationContext(priorContext);}
            Entry managed=deck.Entries[0];store.ReleaseToDesktopAsync(deck,managed).GetAwaiter().GetResult();
            Check(File.ReadAllText(file)=="roundtrip-content"&&deck.Entries.Count==0,"drag back restores real file and removes panel entry");

            string folder=Path.Combine(desktop,"folder-roundtrip");Directory.CreateDirectory(Path.Combine(folder,"nested"));File.WriteAllText(Path.Combine(folder,"nested","child.txt"),"child-data");
            Entry directoryItem=store.CollectAsync(deck,folder).GetAwaiter().GetResult();
            Check(!Directory.Exists(folder)&&File.ReadAllText(Path.Combine(directoryItem.Path,"nested","child.txt"))=="child-data","collect moves complete directory tree");
            store.ReleaseToFolderAsync(deck,directoryItem,destination).GetAwaiter().GetResult();
            Check(deck.Entries.Count==0&&File.ReadAllText(Path.Combine(destination,"folder-roundtrip","nested","child.txt"))=="child-data","drag out directory to arbitrary folder moves tree and removes entry");
            string shared=Path.Combine(common,"public.txt");File.WriteAllText(shared,"public-data");Entry publicItem=store.CollectAsync(deck,shared).GetAwaiter().GetResult();
            store.ReleaseToFolderAsync(deck,publicItem,desktop).GetAwaiter().GetResult();Check(!File.Exists(shared)&&File.ReadAllText(Path.Combine(desktop,"public.txt"))=="public-data","public desktop source can be explicitly collected and released to current desktop");
            string explorerSource=Path.Combine(root,"ExplorerSource");Directory.CreateDirectory(explorerSource);string explorerFile=Path.Combine(explorerSource,"explorer.txt");File.WriteAllText(explorerFile,"explorer-payload");
            Check(store.IsCollectablePath(explorerFile),"explicit Explorer file is collectable outside desktop");
            Entry explorerItem=store.CollectAsync(deck,explorerFile).GetAwaiter().GetResult();
            Check(explorerItem.Managed&&!File.Exists(explorerFile)&&File.ReadAllText(explorerItem.Path)=="explorer-payload","explicit Explorer drag collects real file without copying");
            store.ReleaseToFolderAsync(deck,explorerItem,destination).GetAwaiter().GetResult();
            Check(File.ReadAllText(Path.Combine(destination,"explorer.txt"))=="explorer-payload"&&deck.Entries.Count==0,"Explorer-source collection releases to selected real folder");
            string menuFile=Path.Combine(explorerSource,"menu-only.txt");File.WriteAllText(menuFile,"menu-data");Entry menuItem=store.AddReference(deck,menuFile);
            Throws(delegate{store.MoveIntoStorage(deck,menuItem);},"legacy collect menu keeps desktop-only restriction");store.RemoveReference(deck,menuItem);
            Check(!store.IsCollectablePath(Path.GetPathRoot(desktop)),"filesystem root cannot be collected");
            string selfFolder=Path.Combine(explorerSource,"self-folder"),selfChild=Path.Combine(selfFolder,"child");Directory.CreateDirectory(selfChild);Entry selfReference=store.AddReference(deck,selfFolder);
            Throws(delegate{store.ReleaseToFolderAsync(deck,selfReference,selfChild).GetAwaiter().GetResult();},"drag-out refuses moving a folder into its descendant");
            Check(Directory.Exists(selfChild)&&deck.Entries.Contains(selfReference),"self-descendant rejection retains original folder and reference");store.RemoveReference(deck,selfReference);

            string conflict=Path.Combine(desktop,"conflict.txt");File.WriteAllText(conflict,"managed-data");Entry conflictItem=store.CollectAsync(deck,conflict).GetAwaiter().GetResult();File.WriteAllText(Path.Combine(destination,"conflict.txt"),"existing-data");
            Throws(delegate{store.ReleaseToFolderAsync(deck,conflictItem,destination).GetAwaiter().GetResult();},"drag-out conflict refuses overwrite");
            Check(conflictItem.Managed&&deck.Entries.Contains(conflictItem)&&File.ReadAllText(conflictItem.Path)=="managed-data"&&File.ReadAllText(Path.Combine(destination,"conflict.txt"))=="existing-data","failed drag-out preserves managed file entry and destination");
            Throws(delegate{store.ReleaseToFolderAsync(deck,conflictItem,store.Root).GetAwaiter().GetResult();},"drag-out into program storage is rejected");
            store.ReleaseToDesktopAsync(deck,conflictItem).GetAwaiter().GetResult();
            Entry legacy=store.AddReference(deck,file);store.ReleaseToFolderAsync(deck,legacy,desktop).GetAwaiter().GetResult();
            Check(File.ReadAllText(file)=="roundtrip-content"&&deck.Entries.Count==0,"legacy reference dragged to its existing folder removes only panel entry");
            legacy=store.AddReference(deck,file);store.ReleaseToFolderAsync(deck,legacy,destination).GetAwaiter().GetResult();
            Check(!File.Exists(file)&&File.ReadAllText(Path.Combine(destination,"roundtrip.txt"))=="roundtrip-content"&&deck.Entries.Count==0,"explicit legacy-reference drag to another folder performs real move");

            string locked=Path.Combine(desktop,"locked.txt");File.WriteAllText(locked,"locked-source");
            using(var held=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.Read))Throws(delegate{store.CollectAsync(deck,locked).GetAwaiter().GetResult();},"filesystem move failure is reported");
            Check(File.ReadAllText(locked)=="locked-source"&&deck.Entries.Count==0&&!store.HasPendingTransfer,"failed new collect leaves original and removes provisional reference");

            string journalSource=Path.Combine(desktop,"journal-collect.txt");File.WriteAllText(journalSource,"journal-collect-data");Entry journalItem=store.AddReference(deck,journalSource);
            string journalTarget=Path.Combine(store.Root,"files",deck.Id,journalItem.Id,"journal-collect.txt");Directory.CreateDirectory(Path.GetDirectoryName(journalTarget));
            WriteJournal(store,deck,journalItem,journalSource,journalTarget,false,false,true);File.Move(journalSource,journalTarget);
            store=new Store(store.Root,desktop,common);store.Load();deck=store.State.Decks[0];journalItem=deck.Entries.Single();
            Check(journalItem.Managed&&journalItem.Path==journalTarget,"startup recovers collect after physical move before layout commit");
            MoveJournal completedRelease=WriteJournal(store,deck,journalItem,journalTarget,journalSource,true,true,false);File.Move(journalTarget,journalSource);
            store=new Store(store.Root,desktop,common);store.Load();deck=store.State.Decks[0];
            Check(deck.Entries.Count==0&&File.ReadAllText(journalSource)=="journal-collect-data","startup recovers restored file and removes panel entry exactly once");
            // Replay an already committed removal with its uncleared journal.
            Store.WriteAtomic(Path.Combine(store.Root,"pending-move.json"),completedRelease);
            store=new Store(store.Root,desktop,common);store.Load();deck=store.State.Decks[0];Check(deck.Entries.Count==0,"startup tolerates already committed removal with uncleared journal");
            string abortSource=Path.Combine(desktop,"prepared-only.txt");File.WriteAllText(abortSource,"untouched");Entry abortItem=store.AddReference(deck,abortSource);
            string abortTarget=Path.Combine(store.Root,"files",deck.Id,abortItem.Id,"prepared-only.txt");Directory.CreateDirectory(Path.GetDirectoryName(abortTarget));
            MoveJournal aborted=WriteJournal(store,deck,abortItem,abortSource,abortTarget,false,false,true);
            store=new Store(store.Root,desktop,common);store.Load();deck=store.State.Decks[0];Check(deck.Entries.Count==0&&File.ReadAllText(abortSource)=="untouched","startup aborts prepared collect without leaving provisional reference");
            Store.WriteAtomic(Path.Combine(store.Root,"pending-move.json"),aborted);
            store=new Store(store.Root,desktop,common);store.Load();deck=store.State.Decks[0];Check(deck.Entries.Count==0,"startup cleanup of aborted provisional reference is idempotent");

            string commitSource=Path.Combine(desktop,"commit-failure.txt");File.WriteAllText(commitSource,"durable-payload");Entry commitItem=store.AddReference(deck,commitSource);
            using(var holdLayout=new FileStream(Path.Combine(store.Root,"layout.json"),FileMode.Open,FileAccess.Read,FileShare.Read))Throws(delegate{store.CollectAsync(deck,commitSource).GetAwaiter().GetResult();},"collect commit persistence failure is reported");
            Check(store.HasPendingTransfer&&commitItem.Managed&&!File.Exists(commitSource)&&File.Exists(commitItem.Path),"failed collect commit keeps managed payload and recovery journal");
            store=new Store(store.Root,desktop,common);store.Load();deck=store.State.Decks[0];commitItem=deck.Entries.Single();
            Check(commitItem.Managed&&!store.HasPendingTransfer,"startup completes collect commit after persistence failure");
            using(var holdLayout=new FileStream(Path.Combine(store.Root,"layout.json"),FileMode.Open,FileAccess.Read,FileShare.Read))Throws(delegate{store.ReleaseToFolderAsync(deck,commitItem,destination).GetAwaiter().GetResult();},"release commit persistence failure is reported");
            Check(store.HasPendingTransfer&&deck.Entries.Contains(commitItem)&&commitItem.Managed&&File.ReadAllText(Path.Combine(destination,"commit-failure.txt"))=="durable-payload","failed release commit retains entry and completed physical payload for recovery");
            store=new Store(store.Root,desktop,common);store.Load();deck=store.State.Decks[0];Check(deck.Entries.Count==0&&!store.HasPendingTransfer,"startup completes release removal after persistence failure");
            string retrySource=Path.Combine(desktop,"retry-in-process.txt");File.WriteAllText(retrySource,"retry-payload");Entry retryItem=store.AddReference(deck,retrySource);
            using(var holdLayout=new FileStream(Path.Combine(store.Root,"layout.json"),FileMode.Open,FileAccess.Read,FileShare.Read)) {
                Throws(delegate{store.CollectAsync(deck,retrySource).GetAwaiter().GetResult();},"in-process collect recovery begins from actual persistence failure");
                Throws(delegate{store.RetryPendingTransfer();},"retry still reports an unresolved persistence failure");
                Check(store.HasPendingTransfer&&File.Exists(retryItem.Path),"failed recovery retains journal and collected payload");
            }
            store.RetryPendingTransfer();Check(!store.HasPendingTransfer&&retryItem.Managed,"failed collect commit can recover in process while exit stays protected");
            using(var holdLayout=new FileStream(Path.Combine(store.Root,"layout.json"),FileMode.Open,FileAccess.Read,FileShare.Read)) {
                Throws(delegate{store.ReleaseToFolderAsync(deck,retryItem,destination).GetAwaiter().GetResult();},"in-process release recovery begins from actual persistence failure");
                Throws(delegate{store.RetryPendingTransfer();},"release retry still reports an unresolved persistence failure");
                Check(store.HasPendingTransfer&&deck.Entries.Contains(retryItem)&&retryItem.Managed,"failed release retry restores visible managed entry and journal");
            }
            store.RetryPendingTransfer();Check(!store.HasPendingTransfer&&deck.Entries.Count==0&&ExitProtection.Reason(store.State,false,false)==null,"failed final release recovers in process and unlocks exit");
            var retryReload=new Store(store.Root,desktop,common);retryReload.Load();
            Check(!retryReload.HasPendingTransfer&&retryReload.State.Decks[0].Entries.Count==0,"repeated release recovery persists removal before journal clearing and survives reload");
            var selfStore=new Store(Path.Combine(desktop,"Program"),desktop,common);selfStore.Load();
            Throws(delegate{selfStore.CollectAsync(selfStore.State.Decks[0],selfStore.Root).GetAwaiter().GetResult();},"program cannot collect its own workspace");
            return checks;
        }
        static MoveJournal WriteJournal(Store store,Deck deck,Entry item,string source,string target,bool restore,bool remove,bool abort) {
            var journal=new MoveJournal{DeckId=deck.Id,Item=new Entry{Id=item.Id,Path=item.Path,OriginalPath=item.OriginalPath,Managed=item.Managed,Name=item.Name},Source=source,Destination=target,Restore=restore,RemoveAfterRestore=remove,RemoveReferenceOnAbort=abort};
            Store.WriteAtomic(Path.Combine(store.Root,"pending-move.json"),journal);return journal;
        }
        sealed class QueuedContext:SynchronizationContext {
            readonly Queue<Action> actions=new Queue<Action>();
            public override void Post(SendOrPostCallback callback,object state){lock(actions)actions.Enqueue(delegate{callback(state);});}
            internal void Complete(Task task) {
                var clock=Stopwatch.StartNew();while(!task.IsCompleted) {
                    Action action=null;lock(actions)if(actions.Count>0)action=actions.Dequeue();if(action!=null)action();else Thread.Sleep(1);
                    if(clock.ElapsedMilliseconds>5000)throw new TimeoutException("Queued UI transfer did not complete.");
                }
                task.GetAwaiter().GetResult();
            }
        }
    }
}
