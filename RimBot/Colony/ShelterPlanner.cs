using Verse;
namespace RimBot.Colony
{
    // Compatibility with prototype saves only. No planning or construction behavior remains.
    public sealed class ShelterPlanner : MapComponent
    {
        private string activeId="";
        public ShelterPlanner(Map map):base(map) { }
        public override void ExposeData() { Scribe_Values.Look(ref activeId,"rimBotShelterProject",""); }
    }
}
