namespace RimBot.Colony
{
    public static class PlacementRange
    {
        public static bool Near(int baseX,int baseZ,int x,int z,int width,int height)
        {
            double dx=x+(width-1)/2.0-baseX,dz=z+(height-1)/2.0-baseZ;
            return dx*dx+dz*dz<=32*32;
        }
    }
}
