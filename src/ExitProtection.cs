using System.Linq;

namespace ClearFolder {
    internal static class ExitProtection {
        internal static string Reason(Settings settings,bool transferBusy,bool pendingTransfer) {
            if(transferBusy||pendingTransfer)return "文件移动尚未完成";
            int count=settings.Decks.Sum(deck=>deck.Entries.Count(entry=>entry.Managed));
            return count>0?"还有 "+count+" 个已收纳项目":null;
        }
    }
}
