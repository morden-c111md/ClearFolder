using System;
using System.IO;

namespace ClearFolder.Tests {
    public static class DropDestinationTests {
        static int checks;
        static void Check(bool value,string name) {
            if(!value)throw new Exception("FAIL: "+name);
            checks++;Console.WriteLine("PASS: "+name);
        }
        public static int Run(string root) {
            checks=0;root=Path.GetFullPath(root);
            string user=Path.Combine(root,"user-desktop"),common=Path.Combine(root,"public-desktop");
            string ordinary=Path.Combine(user,"Folder"),shared=Path.Combine(common,"Shared Folder");
            string unicode=Path.Combine(user,"资料文件夹");
            Directory.CreateDirectory(ordinary);Directory.CreateDirectory(shared);Directory.CreateDirectory(unicode);
            Directory.CreateDirectory(Path.Combine(user,"Duplicate"));Directory.CreateDirectory(Path.Combine(common,"Duplicate"));
            Directory.CreateDirectory(Path.Combine(user,"Recycle Bin"));
            string file=Path.Combine(user,"file.txt");File.WriteAllText(file,"synthetic-destination-test");
            Check(DropDestinations.NormalizeDirectory(user)==user,"destination accepts existing absolute directory");
            Check(DropDestinations.NormalizeDirectory(file)==null,"destination rejects ordinary file as directory");
            Check(DropDestinations.NormalizeDirectory(Path.Combine(root,"missing"))==null,"destination rejects missing directory");
            foreach(string invalid in new[]{null,"","relative",@"E:relative",@"\relative","::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",@"\\.\C:\",@"\\?\C:\"})
                Check(DropDestinations.NormalizeDirectory(invalid)==null,"destination rejects virtual, relative or device path: "+(invalid??"null"));
            Check(DropDestinations.ResolveDesktopName("Folder",user,common)==ordinary,"desktop folder resolves to exact existing user child");
            Check(DropDestinations.ResolveDesktopName("Shared Folder",user,common)==shared,"desktop folder resolves to exact existing public child");
            Check(DropDestinations.ResolveDesktopName("资料文件夹",user,common)==unicode,"desktop folder resolution preserves Unicode names");
            Check(DropDestinations.ResolveDesktopName("Folder",user,user)==ordinary,"duplicate roots do not create false destination ambiguity");
            foreach(string invalid in new[]{null,"",".","..",@"..\Folder",@"Folder\child","Folder:stream","file.txt","missing","Duplicate","Recycle Bin","回收站","This PC","此电脑"})
                Check(DropDestinations.ResolveDesktopName(invalid,user,common)==null,"desktop rejects unsafe, file, virtual or ambiguous item: "+(invalid??"null"));
            Check(File.ReadAllText(file)=="synthetic-destination-test"&&Directory.Exists(ordinary),"destination classification never moves or modifies source items");
            return checks;
        }
    }
}
