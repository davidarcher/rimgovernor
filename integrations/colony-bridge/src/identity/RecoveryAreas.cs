using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class RecoveryAreaClaim : IExposable
    {
        public Pawn Pawn;
        public Area Before, Assigned;
        public string Owner;
        public int Until;
        public void ExposeData()
        {
            Scribe_References.Look(ref Pawn, "pawn");
            Scribe_References.Look(ref Before, "before");
            Scribe_References.Look(ref Assigned, "assigned");
            Scribe_Values.Look(ref Owner, "owner");
            Scribe_Values.Look(ref Until, "until");
        }
    }

    // A saved, bounded restriction lease. Expiry/load releases only our current setting.
    public sealed class RecoveryAreas : GameComponent
    {
        public List<RecoveryAreaClaim> Claims = new List<RecoveryAreaClaim>();
        public List<Pawn> Overrides = new List<Pawn>();
        public RecoveryAreas(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Claims, "rimbotRecoveryAreas", LookMode.Deep);
            Scribe_Collections.Look(ref Overrides, "rimbotRecoveryAreaOverrides", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Claims == null) Claims = new List<RecoveryAreaClaim>();
                if (Overrides == null) Overrides = new List<Pawn>();
            }
        }
        public override void GameComponentTick()
        {
            if (Claims == null) Claims = new List<RecoveryAreaClaim>();
            if (Overrides == null) Overrides = new List<Pawn>();
            foreach (var claim in Claims.ToList())
            {
                var pawn = claim.Pawn;
                if (pawn?.playerSettings == null || pawn.Map == null) { Claims.Remove(claim); continue; }
                var current = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
                if (current != claim.Assigned) { Overrides.Add(pawn); Claims.Remove(claim); continue; }
                var conditions = new List<GameCondition>();
                pawn.Map.gameConditionManager.GetAllGameConditionsAffectingMap(pawn.Map, conditions);
                bool expired = claim.Until <= Find.TickManager.TicksGame
                    || claim.Owner != Current.Game.GetComponent<ColonyIdentity>().LoadToken
                    || !conditions.Any(c => c is GameCondition_ToxicFallout);
                if (expired)
                {
                    Claims.Remove(claim);
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = claim.Before;
                }
            }
        }
    }
}
