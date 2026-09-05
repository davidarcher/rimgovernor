using Verse;
namespace RimBot
{
    [StaticConstructorOnStartup]
    public static class ModEntryPoint
    {
        static ModEntryPoint()
        {
            Log.Message("[RimBot Manager] Colony manager loaded. No gameplay patches or pawn lifecycle overrides.");
        }
    }
}
