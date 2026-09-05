using System;
using System.Collections.Generic;
namespace RimBot.Colony
{
    public sealed class RoomPart
    {
        public string DefName;
        public int X,Z;
        public RoomPart(string name,int x,int z) { DefName=name; X=x; Z=z; }
    }
    public static class RoomLayout
    {
        // Seven cells square: a complete perimeter, south doorway, and a clear central aisle.
        public static List<RoomPart> Create(int beds)
        {
            if(beds<0 || beds>4) throw new ArgumentException("A room supports zero to four beds.");
            var parts=new List<RoomPart>();
            for(int x=0;x<7;x++) for(int z=0;z<7;z++)
                if(x==0 || x==6 || z==0 || z==6)
                    parts.Add(new RoomPart(x==3 && z==0?"Door":"Wall",x,z));
            int[] columns={1,2,4,5};
            for(int i=0;i<beds;i++) parts.Add(new RoomPart("Bed",columns[i],3));
            return parts;
        }
    }
}
