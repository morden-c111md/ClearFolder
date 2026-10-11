using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace ClearFolder {
    [DataContract] public sealed class Entry {
        [DataMember] public string Id=Guid.NewGuid().ToString("N");
        [DataMember] public string Path;
        [DataMember] public string OriginalPath;
        [DataMember] public string Name;
        [DataMember] public bool Managed;
    }
    [DataContract] public sealed class Deck {
        [DataMember] public string Id=Guid.NewGuid().ToString("N");
        [DataMember] public string Name="收纳文件夹";
        [DataMember] public int X=280;
        [DataMember] public int Y=100;
        [DataMember] public string PlaceholderPath;
        [DataMember] public List<Entry> Entries=new List<Entry>();
    }
    [DataContract] public sealed class Settings {
        [DataMember] public int Version=1;
        [DataMember] public double Contrast=1.12;
        [DataMember] public double RippleStrength=1.8;
        [DataMember] public List<Deck> Decks=new List<Deck>();
    }
    [DataContract] public sealed class MoveJournal {
        [DataMember] public string Source;
        [DataMember] public string Destination;
        [DataMember] public string DeckId;
        [DataMember] public Entry Item;
        [DataMember] public bool Restore;
        [DataMember] public bool RemoveAfterRestore;
        [DataMember] public bool RemoveReferenceOnAbort;
    }
    public sealed class Store {
        public readonly string Root;
        internal string DesktopDirectory { get { return desktop; } }
        readonly string desktop,publicDesktop;
        bool transferActive,unresolvedTransfer;
        public Settings State;
        public Store(string root):this(root,Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)){}
        internal Store(string root,string currentDesktop,string commonDesktop) { Root=System.IO.Path.GetFullPath(root);desktop=System.IO.Path.GetFullPath(currentDesktop);publicDesktop=System.IO.Path.GetFullPath(commonDesktop); }
        public bool HasPendingTransfer {get{return transferActive||unresolvedTransfer;}}
        internal bool IsTransferActive {get{return transferActive;}}
        internal void RetryPendingTransfer() {
            if(transferActive)throw new InvalidOperationException("文件移动正在完成，请稍后再恢复记录。");
            RecoverJournal();unresolvedTransfer=false;
        }
        public void Load() {
            Directory.CreateDirectory(Root);
            string config=System.IO.Path.Combine(Root,"layout.json");
            State=File.Exists(config)?Read<Settings>(config):new Settings();
            if(State==null||State.Decks==null||State.Version!=1)throw new InvalidDataException("布局版本或内容无效，原文件已保留。");
            if(State.Decks.Count>30)throw new InvalidDataException("收纳组数量异常。");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var deck in State.Decks) {
                Guid parsed;
                if(deck==null||!Guid.TryParseExact(deck.Id,"N",out parsed)||!ids.Add(deck.Id)||deck.Entries==null)throw new InvalidDataException("布局中的文件夹信息无效。");
                if(string.IsNullOrWhiteSpace(deck.Name))deck.Name="收纳文件夹";
                foreach(var item in deck.Entries) {
                    if(item==null||!Guid.TryParseExact(item.Id,"N",out parsed)||string.IsNullOrWhiteSpace(item.Path))throw new InvalidDataException("布局中的文件引用无效。");
                    item.Path=System.IO.Path.GetFullPath(item.Path);
                    if(item.Managed&&!Inside(item.Path,System.IO.Path.Combine(Root,"files",deck.Id)))throw new InvalidDataException("收纳文件路径超出指定存储目录。");
                }
            }
            State.Contrast=Math.Max(1,Math.Min(1.3,State.Contrast));
            State.RippleStrength=Math.Max(0,Math.Min(4,State.RippleStrength));
            RecoverJournal();
            if(State.Decks.Count==0){State.Decks.Add(new Deck());Save();}
        }
        public static bool Exists(string path){return File.Exists(path)||Directory.Exists(path);}
        public static bool Inside(string path,string root) {
            string p=System.IO.Path.GetFullPath(path).TrimEnd('\\');
            string r=System.IO.Path.GetFullPath(root).TrimEnd('\\');
            return p.StartsWith(r+"\\",StringComparison.OrdinalIgnoreCase);
        }
        public void Save(){EnsureMutable();SaveCore();}
        void SaveCore(){WriteAtomic(System.IO.Path.Combine(Root,"layout.json"),State);}
        void EnsureMutable(){if(HasPendingTransfer)throw new InvalidOperationException("文件移动尚未完成，请稍候；存在恢复记录时可从托盘选择“重试完成移动记录”。");}
        public Entry AddReference(Deck deck,string path) {
            EnsureMutable();EnsureEntryOwner(deck,null);
            path=System.IO.Path.GetFullPath(path);
            if(!Exists(path))throw new FileNotFoundException("该项目已不存在。",path);
            var prior=deck.Entries.FirstOrDefault(e=>string.Equals(e.Path,path,StringComparison.OrdinalIgnoreCase));
            if(prior!=null)return prior;
            var item=new Entry {Path=path,Name=System.IO.Path.GetFileNameWithoutExtension(path)};
            if(Directory.Exists(path))item.Name=System.IO.Path.GetFileName(path);
            deck.Entries.Add(item);
            try {Save();}catch{deck.Entries.Remove(item);throw;}
            return item;
        }
        public void RemoveReference(Deck deck,Entry item) {
            EnsureMutable();EnsureEntryOwner(deck,item);
            if(item.Managed)throw new InvalidOperationException("此项目已经移入收纳目录，请先恢复原位置。");
            int index=deck.Entries.IndexOf(item); if(index<0)return;
            deck.Entries.RemoveAt(index);try{Save();}catch{deck.Entries.Insert(index,item);throw;}
        }
        public void MoveIntoStorage(Deck deck,Entry item) {
            EnsureMutable();EnsureEntryOwner(deck,item);
            if(item.Managed)return;
            ValidateCollectable(item.Path,true);
            ExecuteMove(deck,item,item.Path,StorageTarget(deck,item),false,false,false);
        }
        public void Restore(Deck deck,Entry item) {
            EnsureMutable();EnsureEntryOwner(deck,item);
            if(!item.Managed)return;
            ValidateRestore(deck,item);
            ExecuteMove(deck,item,item.Path,item.OriginalPath,true,false,false);
        }
        public bool IsCollectablePath(string path) {
            try{ValidateCollectable(System.IO.Path.GetFullPath(path));return true;}
            catch(Exception ex){if(ex is OutOfMemoryException||ex is AccessViolationException)throw;return false;}
        }
        public async Task<Entry> CollectAsync(Deck deck,string path) {
            EnsureMutable();EnsureEntryOwner(deck,null);path=System.IO.Path.GetFullPath(path);ValidateCollectable(path);
            Entry item=deck.Entries.FirstOrDefault(e=>string.Equals(e.Path,path,StringComparison.OrdinalIgnoreCase));
            bool created=item==null;if(created)item=AddReference(deck,path);
            try{await ExecuteMoveAsync(deck,item,path,StorageTarget(deck,item),false,false,created);return item;}
            catch(Exception failure) {
                if(created&&!item.Managed&&Exists(path)&&!HasPendingTransfer) {
                    try{RemoveReference(deck,item);}catch(Exception cleanup){throw new IOException("收纳失败，原文件保留；清理临时引用时写入布局失败。",new AggregateException(failure,cleanup));}
                }
                throw;
            }
        }
        public async Task ReleaseToDesktopAsync(Deck deck,Entry item) {
            EnsureMutable();EnsureEntryOwner(deck,item);
            if(item.Managed){ValidateRestore(deck,item);await ReleaseToFolderAsync(deck,item,System.IO.Path.GetDirectoryName(item.OriginalPath));}
            else await ReleaseToFolderAsync(deck,item,desktop);
        }
        public async Task ReleaseToFolderAsync(Deck deck,Entry item,string directory) {
            EnsureMutable();EnsureEntryOwner(deck,item);directory=System.IO.Path.GetFullPath(directory);
            if(!Directory.Exists(directory))throw new DirectoryNotFoundException("拖放目标文件夹已不存在。");
            if(string.Equals(directory,Root,StringComparison.OrdinalIgnoreCase)||Inside(directory,Root))throw new InvalidOperationException("不能拖入程序的布局或收纳存储目录。");
            string source=System.IO.Path.GetFullPath(item.Path);
            if(Directory.Exists(source)&&(string.Equals(directory,source,StringComparison.OrdinalIgnoreCase)||Inside(directory,source)))throw new InvalidOperationException("不能把文件夹拖入自身或其子文件夹。");
            if(item.Managed)ValidateRestore(deck,item);else ValidateCollectable(source);
            string filename=System.IO.Path.GetFileName(item.Managed?item.OriginalPath:source),target=System.IO.Path.Combine(directory,filename);
            if(string.Equals(source,target,StringComparison.OrdinalIgnoreCase)) {
                if(item.Managed)throw new InvalidOperationException("目标仍在收纳存储目录中。");
                RemoveReference(deck,item);return;
            }
            await ExecuteMoveAsync(deck,item,source,target,true,true,false);
        }
        void EnsureEntryOwner(Deck deck,Entry item) {
            if(State==null||deck==null||!State.Decks.Contains(deck)||item!=null&&!deck.Entries.Contains(item))throw new InvalidOperationException("收纳项目不属于当前布局。");
        }
        void ValidateCollectable(string path,bool desktopOnly=false) {
            path=System.IO.Path.GetFullPath(path);string parent=System.IO.Path.GetDirectoryName(path);
            if(string.IsNullOrEmpty(parent)||string.Equals(path.TrimEnd('\\'),System.IO.Path.GetPathRoot(path).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("不能收纳磁盘或共享根目录。");
            if(desktopOnly&&!string.Equals(parent,desktop,StringComparison.OrdinalIgnoreCase)&&!string.Equals(parent,publicDesktop,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("这个菜单仅收纳桌面顶层项目；资源管理器中的项目可明确拖入收纳。");
            if(!Exists(path))throw new FileNotFoundException("源项目不存在。",path);
            if(string.Equals(path,Root,StringComparison.OrdinalIgnoreCase)||Inside(Root,path)||Inside(path,Root))throw new InvalidOperationException("不能收纳程序自身或包含程序的目录。");
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("暂不收纳符号链接或联接点，原项目保持不变。");
        }
        static void ValidateNoReparse(string path) {
            var pending=new Stack<string>();pending.Push(path);
            while(pending.Count>0) {
                string current=pending.Pop();FileAttributes attributes=File.GetAttributes(current);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("暂不收纳包含符号链接或联接点的项目，原项目保持不变。");
                if((attributes&FileAttributes.Directory)!=0)foreach(string child in Directory.EnumerateFileSystemEntries(current))pending.Push(child);
            }
        }
        string StorageTarget(Deck deck,Entry item) {
            string directory=System.IO.Path.Combine(Root,"files",deck.Id,item.Id);Directory.CreateDirectory(directory);
            return System.IO.Path.Combine(directory,System.IO.Path.GetFileName(item.Path));
        }
        void ValidateRestore(Deck deck,Entry item) {
            if(string.IsNullOrWhiteSpace(item.OriginalPath))throw new InvalidDataException("缺少原位置，不能恢复。");
            if(!Inside(item.Path,System.IO.Path.Combine(Root,"files",deck.Id,item.Id)))throw new InvalidOperationException("收纳路径无效。");
        }
        MoveJournal PrepareMove(Deck deck,Entry item,string source,string target,bool restore,bool removeAfterRestore,bool removeReferenceOnAbort) {
            EnsureMutable();
            string pending=System.IO.Path.Combine(Root,"pending-move.json");
            if(File.Exists(pending)&&Read<MoveJournal>(pending)!=null){unresolvedTransfer=true;throw new IOException("上一次移动记录尚未完成，请从托盘选择“重试完成移动记录”。");}
            if(!Exists(source))throw new FileNotFoundException("源项目不存在。",source);
            if(Exists(target))throw new IOException("目标位置已有同名项目，已拒绝覆盖："+target);
            if(!Directory.Exists(System.IO.Path.GetDirectoryName(target)))throw new DirectoryNotFoundException("目标父目录不存在。");
            var journal=new MoveJournal {Source=source,Destination=target,DeckId=deck.Id,Item=new Entry{Id=item.Id,Path=item.Path,Name=item.Name,Managed=item.Managed,OriginalPath=item.OriginalPath},Restore=restore,RemoveAfterRestore=removeAfterRestore,RemoveReferenceOnAbort=removeReferenceOnAbort};
            WriteAtomic(pending,journal);
            transferActive=true;return journal;
        }
        void AbortMove(MoveJournal journal) {
            string pending=System.IO.Path.Combine(Root,"pending-move.json");
                // A partial cross-volume directory move must retain its journal and both trees.
            unresolvedTransfer=true;
            if(Exists(journal.Source)&&!Exists(journal.Destination)){WriteAtomic<MoveJournal>(pending,null);unresolvedTransfer=false;}
        }
        void CommitMove(Deck deck,Entry item,MoveJournal journal) {
            unresolvedTransfer=true;
            string priorPath=item.Path,priorOriginal=item.OriginalPath;bool priorManaged=item.Managed;int index=deck.Entries.IndexOf(item);
            ApplyMove(item,journal);if(journal.RemoveAfterRestore)deck.Entries.Remove(item);
            try{SaveCore();}
            catch {
                // Keep the pending item visible in memory until durable recovery resolves it.
                if(journal.RemoveAfterRestore){deck.Entries.Insert(index,item);item.Path=priorPath;item.OriginalPath=priorOriginal;item.Managed=priorManaged;}
                throw;
            }
            WriteAtomic<MoveJournal>(System.IO.Path.Combine(Root,"pending-move.json"),null);unresolvedTransfer=false;
        }
        void ExecuteMove(Deck deck,Entry item,string source,string target,bool restore,bool removeAfterRestore,bool removeReferenceOnAbort) {
            MoveJournal journal=PrepareMove(deck,item,source,target,restore,removeAfterRestore,removeReferenceOnAbort);
            try {try{Relocate(source,target);}catch{AbortMove(journal);throw;}CommitMove(deck,item,journal);}
            finally{transferActive=false;}
        }
        async Task ExecuteMoveAsync(Deck deck,Entry item,string source,string target,bool restore,bool removeAfterRestore,bool removeReferenceOnAbort) {
            MoveJournal journal=PrepareMove(deck,item,source,target,restore,removeAfterRestore,removeReferenceOnAbort);
            try {
                try{await Task.Run(delegate{Relocate(source,target);});}
                catch{AbortMove(journal);throw;}
                CommitMove(deck,item,journal);
            }finally{transferActive=false;}
        }
        static void ApplyMove(Entry item,MoveJournal j) {
            if(j.Restore){item.Path=j.Destination;item.Managed=false;item.OriginalPath=null;}
            else {item.OriginalPath=j.Source;item.Path=j.Destination;item.Managed=true;}
        }
        internal static void Relocate(string source,string target) {
            if(Exists(target))throw new IOException("目标已存在，拒绝覆盖。");
            ValidateNoReparse(source);
            if(Directory.Exists(source))Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(source,target,false);
            else File.Move(source,target);
        }
        void RecoverJournal() {
            string pending=System.IO.Path.Combine(Root,"pending-move.json");
            if(!File.Exists(pending))return;
            MoveJournal j=Read<MoveJournal>(pending);if(j==null)return;
            unresolvedTransfer=true;
            var deck=State.Decks.FirstOrDefault(d=>d.Id==j.DeckId);
            if(deck==null||j.Item==null||string.IsNullOrWhiteSpace(j.Source)||string.IsNullOrWhiteSpace(j.Destination))throw new InvalidDataException("移动恢复记录无效，已保留。");
            ValidateJournal(deck,j);
            var item=deck.Entries.FirstOrDefault(e=>e.Id==j.Item.Id);
            bool removedRestore=j.Restore&&j.RemoveAfterRestore&&!Exists(j.Source)&&Exists(j.Destination);
            bool removedAbort=j.RemoveReferenceOnAbort&&Exists(j.Source)&&!Exists(j.Destination);
            if(item==null&&!removedRestore&&!removedAbort)throw new InvalidDataException("移动恢复缺少项目，记录已保留。");
            string priorPath=item==null?null:item.Path,priorOriginal=item==null?null:item.OriginalPath;
            bool priorManaged=item!=null&&item.Managed,saved=false;int priorIndex=item==null?-1:deck.Entries.IndexOf(item);
            try {
                if(!Exists(j.Source)&&Exists(j.Destination)) {
                    if(item!=null){ApplyMove(item,j);if(j.RemoveAfterRestore)deck.Entries.Remove(item);}
                }
                else if(!Exists(j.Source)||Exists(j.Destination))throw new IOException("移动恢复存在冲突，请检查 pending-move.json；原记录已保留。");
                else if(j.RemoveReferenceOnAbort&&item!=null&&!item.Managed)deck.Entries.Remove(item);
                // Persist even a replay whose item is already absent before clearing its journal.
                SaveCore();saved=true;WriteAtomic<MoveJournal>(pending,null);unresolvedTransfer=false;
            }catch {
                if(!saved&&item!=null) {
                    item.Path=priorPath;item.OriginalPath=priorOriginal;item.Managed=priorManaged;
                    if(priorIndex>=0&&!deck.Entries.Contains(item))deck.Entries.Insert(Math.Min(priorIndex,deck.Entries.Count),item);
                }
                throw;
            }
        }
        void ValidateJournal(Deck deck,MoveJournal journal) {
            Guid id;
            if(!Guid.TryParseExact(journal.Item.Id,"N",out id)||!string.Equals(System.IO.Path.GetFullPath(journal.Item.Path),System.IO.Path.GetFullPath(journal.Source),StringComparison.OrdinalIgnoreCase)||journal.RemoveAfterRestore&&!journal.Restore||journal.RemoveReferenceOnAbort&&journal.Restore)throw new InvalidDataException("移动恢复中的项目或路径不一致，记录已保留。");
            string owned=System.IO.Path.Combine(Root,"files",deck.Id,journal.Item.Id);
            if(!journal.Restore&&!Inside(journal.Destination,owned)||journal.Restore&&journal.Item.Managed&&!Inside(journal.Source,owned))throw new InvalidDataException("移动恢复路径超出指定项目的收纳目录，记录已保留。");
            if(journal.RemoveAfterRestore&&(string.Equals(System.IO.Path.GetFullPath(journal.Destination),Root,StringComparison.OrdinalIgnoreCase)||Inside(journal.Destination,Root)))throw new InvalidDataException("拖出恢复目标不能位于本程序存储目录，记录已保留。");
            if(string.Equals(System.IO.Path.GetFullPath(journal.Source),System.IO.Path.GetFullPath(journal.Destination),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("移动恢复源路径和目标路径相同，记录已保留。");
        }
        internal static T Read<T>(string path) {
            using(var stream=File.OpenRead(path))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        internal static void WriteAtomic<T>(string path,T value) {
            string temp=path+".new";
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);stream.Flush(true);
            }
            if(File.Exists(path))File.Replace(temp,path,path+".bak",true);else File.Move(temp,path);
        }
    }
}
