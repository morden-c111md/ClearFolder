using System;
using System.Collections.Generic;

namespace ClearFolder {
    // Birth positions are immutable. Moving the pointer creates a new wave rather than
    // restarting the age and center of every previously visible wave.
    internal sealed class RippleState {
        internal sealed class Wave {
            internal readonly double X,Y,Born;
            internal Wave(double x,double y,double born){X=x;Y=y;Born=born;}
        }
        internal const double Lifetime=1.4;
        readonly List<Wave> waves=new List<Wave>();
        double lastBirth=double.NegativeInfinity,lastX,lastY;
        internal IList<Wave> Waves {get{return waves.AsReadOnly();}}
        internal bool Advance(double now) {
            for(int i=waves.Count-1;i>=0;i--)if(now-waves[i].Born>Lifetime)waves.RemoveAt(i);
            return waves.Count>0;
        }
        internal void Pointer(double x,double y,double now,bool inside,bool enabled) {
            Advance(now);
            if(!inside||!enabled)return;
            double dx=x-lastX,dy=y-lastY;
            if(now-lastBirth<0.085||(waves.Count>0&&dx*dx+dy*dy<36))return;
            if(waves.Count==3)waves.RemoveAt(0);
            waves.Add(new Wave(x,y,now));lastBirth=now;lastX=x;lastY=y;
        }
        internal void Clear(){waves.Clear();lastBirth=double.NegativeInfinity;}
    }
}
