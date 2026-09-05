using System;
using System.Collections.Generic;
using System.Linq;
namespace RimBot.Colony
{
    public enum ShelterCellKind { Floor, Wall, Door, Rock, Blocked, Unknown }
    public sealed class ShelterCell
    {
        public ShelterCellKind Kind;
        public bool Roofed,ThickRoof,Occupied;
        public int BedSlots;
    }
    public sealed class ShelterPoint
    {
        public int X,Z;
        public ShelterPoint(int x,int z) { X=x; Z=z; }
    }
    public sealed class ShelterDesign
    {
        public int Width,Height,Reused,ThickRoof,MissingRoof;
        public ShelterPoint Door;
        public List<ShelterPoint> Walls=new List<ShelterPoint>(),Mine=new List<ShelterPoint>(),Beds=new List<ShelterPoint>();
        public bool NewDoor;
        public string Kind => Mine.Count>0?"excavate":Reused>0?"reuse or enclose":"new shelter";
        public int WoodEstimate => Walls.Count*5+(NewDoor?25:0);
    }
    public static class ShelterGeometry
    {
        public static ShelterDesign Evaluate(ShelterCell[,] cells,int sleepers,Func<int,int,bool> outsideAccessible)
        {
            int w=cells.GetLength(0),h=cells.GetLength(1);
            if(w<4 || w>8 || h<4 || h>8 || sleepers<1 || sleepers>6) return null;
            var plan=new ShelterDesign { Width=w,Height=h };
            var openings=new List<ShelterPoint>();
            int existingSlots=0;
            for(int x=0;x<w;x++) for(int z=0;z<h;z++) {
                var c=cells[x,z];
                if(c==null || c.Kind==ShelterCellKind.Unknown || c.Kind==ShelterCellKind.Blocked) return null;
                bool boundary=x==0 || x==w-1 || z==0 || z==h-1;
                if(boundary) {
                    if(c.Occupied) return null;
                    if(c.Kind==ShelterCellKind.Wall || c.Kind==ShelterCellKind.Rock || c.Kind==ShelterCellKind.Door) plan.Reused++;
                    bool corner=(x==0 || x==w-1)&&(z==0 || z==h-1);
                    if(!corner && c.Kind!=ShelterCellKind.Wall && outsideAccessible(x,z)) openings.Add(new ShelterPoint(x,z));
                } else {
                    if(c.Kind==ShelterCellKind.Wall || c.Kind==ShelterCellKind.Door) return null;
                    if(c.Kind==ShelterCellKind.Rock) plan.Mine.Add(new ShelterPoint(x,z));
                    if(c.ThickRoof) plan.ThickRoof++;
                    if(!c.Roofed) plan.MissingRoof++;
                    existingSlots+=c.BedSlots;
                }
            }
            var door=openings.OrderBy(p=>cells[p.X,p.Z].Kind==ShelterCellKind.Door?0:cells[p.X,p.Z].Kind==ShelterCellKind.Floor?1:2)
                .ThenBy(p=>Math.Abs(p.X-w/2)+Math.Abs(p.Z-h/2)).FirstOrDefault();
            if(door==null) return null;
            plan.Door=door; plan.NewDoor=cells[door.X,door.Z].Kind!=ShelterCellKind.Door;
            if(cells[door.X,door.Z].Kind==ShelterCellKind.Rock) plan.Mine.Add(door);
            for(int x=0;x<w;x++) for(int z=0;z<h;z++)
                if((x==0 || z==0 || x==w-1 || z==h-1) && (x!=door.X || z!=door.Z) && cells[x,z].Kind==ShelterCellKind.Floor)
                    plan.Walls.Add(new ShelterPoint(x,z));
            var entry=new ShelterPoint(door.X==0?1:door.X==w-1?w-2:door.X,door.Z==0?1:door.Z==h-1?h-2:door.Z);
            var occupied=new bool[w,h];
            for(int x=1;x<w-1;x++) for(int z=1;z<h-1;z++) occupied[x,z]=cells[x,z].Occupied;
            int needed=Math.Max(0,sleepers-existingSlots);
            var choices=new List<ShelterPoint>();
            for(int x=1;x<w-1;x++) for(int z=1;z<h-2;z++) choices.Add(new ShelterPoint(x,z));
            foreach(var p in choices.OrderByDescending(p=>Math.Abs(p.X-entry.X)+Math.Abs(p.Z-entry.Z))) {
                if(plan.Beds.Count>=needed) break;
                if(occupied[p.X,p.Z] || occupied[p.X,p.Z+1] || p.X==entry.X && (p.Z==entry.Z || p.Z+1==entry.Z)) continue;
                occupied[p.X,p.Z]=occupied[p.X,p.Z+1]=true;
                var reachable=Reachable(occupied,entry);
                if(plan.Beds.Concat(new[]{p}).All(b=>Adjacent(reachable,b,w,h))) plan.Beds.Add(p);
                else occupied[p.X,p.Z]=occupied[p.X,p.Z+1]=false;
            }
            if(plan.Beds.Count<needed) return null;
            return plan;
        }
        private static HashSet<int> Reachable(bool[,] blocked,ShelterPoint entry)
        {
            int w=blocked.GetLength(0),h=blocked.GetLength(1);
            var seen=new HashSet<int>(); var todo=new Queue<ShelterPoint>(); todo.Enqueue(entry);
            while(todo.Count>0) {
                var p=todo.Dequeue();
                if(p.X<1 || p.Z<1 || p.X>=w-1 || p.Z>=h-1 || blocked[p.X,p.Z] || !seen.Add(p.X+p.Z*w)) continue;
                todo.Enqueue(new ShelterPoint(p.X+1,p.Z)); todo.Enqueue(new ShelterPoint(p.X-1,p.Z));
                todo.Enqueue(new ShelterPoint(p.X,p.Z+1)); todo.Enqueue(new ShelterPoint(p.X,p.Z-1));
            }
            return seen;
        }
        private static bool Adjacent(HashSet<int> reachable,ShelterPoint bed,int w,int h)
        {
            return reachable.Contains(bed.X+(bed.Z-1)*w) || reachable.Contains(bed.X+(bed.Z+2)*w) ||
                reachable.Contains(bed.X-1+bed.Z*w) || reachable.Contains(bed.X+1+bed.Z*w) ||
                reachable.Contains(bed.X-1+(bed.Z+1)*w) || reachable.Contains(bed.X+1+(bed.Z+1)*w);
        }
    }
}
