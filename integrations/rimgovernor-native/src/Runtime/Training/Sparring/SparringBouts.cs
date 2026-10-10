#nullable disable
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimGovernor.Runtime
{
    public enum BoutState { Gathering, Fighting }

    // One pawn's place in a bout: the marker reserved for it and where it stands.
    public sealed class BoutSlot
    {
        public Pawn Pawn;
        public Thing Marker;
        public bool Arrived;
        public int Team;
    }

    public sealed class Bout
    {
        public int Id;
        public BoutState State;
        public int FormedTick;
        public int LastArrivalTick;
        public int FightStartTick;
        public readonly List<BoutSlot> Slots = new List<BoutSlot>();

        public BoutSlot SlotOf(Pawn pawn) => Slots.FirstOrDefault(s => s.Pawn == pawn);

        public List<SparringFighter> Fighters() => Slots.Select(s => new SparringFighter
        {
            Id = s.Pawn.thingIDNumber, Team = s.Team, X = s.Pawn.Position.x, Z = s.Pawn.Position.z,
            Standing = s.Pawn.Spawned && !s.Pawn.Dead && !s.Pawn.Downed,
        }).ToList();
    }

    // The per-map bout registry (#2708). Runtime only: keyed on the Map object, so
    // a loaded game starts with none, and never written to the save. A spar job
    // whose bout is gone ends Incompletable (the driver). Formation itself is
    // SparringFormation; this class holds the game-facing state and the timing.
    public sealed class SparringBouts
    {
        public const string WorkTypeName = "RimGovernorTraining";
        public const string MarkerDefName = "RimGovernor_SparringMarker";
        public const string JobName = "RimGovernor_Spar";

        // Gathering becomes fighting when at least two have arrived and none has
        // arrived for this long, or a full four has arrived.
        public const int JoinWindowTicks = 120;
        // A pawn that has not reached its marker this long after the bout formed
        // is dropped and sits out the cooldown.
        public const int InviteTicks = 900;
        public const int NoShowCooldownTicks = 1500;
        // Placeholder stop rule until #2709 ends a bout on its own terms.
        public const int FightTicks = 600;

        private static readonly ConditionalWeakTable<Map, SparringBouts> PerMap = new ConditionalWeakTable<Map, SparringBouts>();

        private readonly Map map;
        private readonly List<Bout> bouts = new List<Bout>();
        private readonly Dictionary<int, int[]> previous = new Dictionary<int, int[]>();
        private readonly Dictionary<int, int> sitOutUntil = new Dictionary<int, int>();
        private int nextBoutId = 1;
        private int upkeepTick = -1;

        private SparringBouts(Map map) { this.map = map; }

        public static SparringBouts For(Map map) => PerMap.GetValue(map, m => new SparringBouts(m));

        public IReadOnlyList<Bout> Bouts => bouts;

        public static ThingDef MarkerDef => DefDatabase<ThingDef>.GetNamedSilentFail(MarkerDefName);

        private static int Now => Find.TickManager.TicksGame;

        public static int Melee(Pawn pawn) => pawn.skills.GetSkill(SkillDefOf.Melee).Level;

        // Who may spar: an adult colonist on the map, able to fight, unhurt, and
        // below the unlocked tier's ceiling. Violent work disabled covers a
        // pacifist trait and a violence restriction; a disabled Melee skill too.
        public static bool Eligible(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || !pawn.IsColonist || pawn.Downed || pawn.Drafted) return false;
            if (pawn.DevelopmentalStage != DevelopmentalStage.Adult || pawn.skills == null || pawn.equipment == null || pawn.inventory == null) return false;
            if (pawn.WorkTagIsDisabled(WorkTags.Violent)) return false;
            var melee = pawn.skills.GetSkill(SkillDefOf.Melee);
            if (melee.TotallyDisabled || melee.Level >= SparringRules.Ceiling) return false;
            var health = pawn.health.hediffSet;
            return health.BleedRateTotal <= 0.01f && health.PainTotal <= 0.1f;
        }

        public Bout BoutOf(Pawn pawn) => bouts.FirstOrDefault(b => b.SlotOf(pawn) != null);

        // The marker a pawn is to spar at, forming bouts first when it is free and
        // others are too; null when it has no place.
        public Thing PlanFor(Pawn pawn)
        {
            Upkeep();
            var bout = BoutOf(pawn);
            if (bout != null) return bout.State == BoutState.Gathering ? bout.SlotOf(pawn).Marker : null;
            if (!Free(pawn)) return null;
            Form();
            bout = BoutOf(pawn);
            return bout?.SlotOf(pawn).Marker;
        }

        private bool Free(Pawn pawn)
        {
            if (!Eligible(pawn) || BoutOf(pawn) != null) return false;
            if (sitOutUntil.TryGetValue(pawn.thingIDNumber, out var until) && Now < until) return false;
            var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(WorkTypeName);
            return work != null && pawn.workSettings != null && pawn.workSettings.WorkIsActive(work);
        }

        private List<Thing> FreeMarkers()
        {
            var def = MarkerDef;
            if (def == null) return new List<Thing>();
            var taken = new HashSet<Thing>(bouts.SelectMany(b => b.Slots.Select(s => s.Marker)));
            return map.listerThings.ThingsOfDef(def).Where(t => t.Spawned && !taken.Contains(t)).ToList();
        }

        private void Form()
        {
            var free = map.mapPawns.FreeColonistsSpawned.Where(Free).ToList();
            var markers = FreeMarkers();
            if (free.Count < SparringFormation.MinSize || markers.Count < SparringFormation.MinSize) return;
            var candidates = free.Select(p => new SparringCandidate
            {
                Id = p.thingIDNumber, Melee = Melee(p),
                Previous = previous.TryGetValue(p.thingIDNumber, out var last) ? last : new int[0],
            }).ToList();
            foreach (var group in SparringFormation.Form(candidates, markers.Count))
            {
                var bout = new Bout { Id = nextBoutId++, State = BoutState.Gathering, FormedTick = Now };
                foreach (var id in group)
                {
                    var pawn = free.First(p => p.thingIDNumber == id);
                    var marker = markers.OrderBy(m => (m.Position - pawn.Position).LengthHorizontalSquared).ThenBy(m => m.thingIDNumber).First();
                    markers.Remove(marker);
                    bout.Slots.Add(new BoutSlot { Pawn = pawn, Marker = marker });
                }
                bouts.Add(bout);
            }
        }

        // The pawn reached its marker.
        public void Arrive(Pawn pawn)
        {
            var bout = BoutOf(pawn);
            if (bout == null || bout.State != BoutState.Gathering) return;
            bout.SlotOf(pawn).Arrived = true;
            bout.LastArrivalTick = Now;
        }

        // The spar job ended (any reason): the pawn gives up its place; a bout left
        // below two pawns is over.
        public void Leave(Pawn pawn)
        {
            var bout = BoutOf(pawn);
            if (bout == null) return;
            bout.Slots.Remove(bout.SlotOf(pawn));
            if (bout.Slots.Count < SparringFormation.MinSize) bouts.Remove(bout);
        }

        // The living, standing member of another team nearest the pawn; null when
        // the pawn is in no fighting bout or none is left.
        public Pawn OpponentOf(Pawn pawn)
        {
            var bout = BoutOf(pawn);
            if (bout == null || bout.State != BoutState.Fighting) return null;
            var fighters = bout.Fighters();
            var id = SparringFormation.Opponent(fighters.First(f => f.Id == pawn.thingIDNumber), fighters);
            return bout.Slots.Select(s => s.Pawn).FirstOrDefault(p => p.thingIDNumber == id);
        }

        // Once per tick, whoever asks: drops pawns that can no longer take part,
        // starts bouts that have gathered and ends bouts that are over.
        public void Upkeep()
        {
            var now = Now;
            if (upkeepTick == now) return;
            upkeepTick = now;
            foreach (var bout in bouts.ToList())
            {
                if (bout.State == BoutState.Gathering) Gather(bout, now); else Fight(bout, now);
            }
        }

        private void Gather(Bout bout, int now)
        {
            foreach (var slot in bout.Slots.ToList())
            {
                if (Eligible(slot.Pawn)) continue;
                bout.Slots.Remove(slot);
            }
            var arrived = bout.Slots.Count(s => s.Arrived);
            var expired = now - bout.FormedTick >= InviteTicks;
            var allHere = arrived == bout.Slots.Count;
            var ready = arrived >= SparringFormation.MinSize
                && (allHere && (bout.Slots.Count == SparringFormation.MaxSize || now - bout.LastArrivalTick >= JoinWindowTicks) || expired);
            if (ready)
            {
                foreach (var slot in bout.Slots.Where(s => !s.Arrived).ToList())
                {
                    sitOutUntil[slot.Pawn.thingIDNumber] = now + NoShowCooldownTicks;
                    bout.Slots.Remove(slot);
                }
                Begin(bout, now);
            }
            else if (expired)
            {
                foreach (var slot in bout.Slots.Where(s => !s.Arrived)) sitOutUntil[slot.Pawn.thingIDNumber] = now + NoShowCooldownTicks;
                bouts.Remove(bout);
            }
            else if (bout.Slots.Count < SparringFormation.MinSize) bouts.Remove(bout);
        }

        private void Begin(Bout bout, int now)
        {
            var members = bout.Slots.Select(s => new SparringCandidate { Id = s.Pawn.thingIDNumber, Melee = Melee(s.Pawn) }).ToList();
            var teams = SparringFormation.Teams(members);
            for (var i = 0; i < teams.Length; i++) bout.Slots[i].Team = teams[i];
            bout.State = BoutState.Fighting;
            bout.FightStartTick = now;
            var ids = bout.Slots.Select(s => s.Pawn.thingIDNumber).ToArray();
            foreach (var id in ids) previous[id] = ids;
        }

        private void Fight(Bout bout, int now)
        {
            foreach (var slot in bout.Slots.ToList())
            {
                if (slot.Pawn.Spawned && !slot.Pawn.Dead && !slot.Pawn.Drafted) continue;
                bout.Slots.Remove(slot);
            }
            if (bout.Slots.Count < SparringFormation.MinSize || now - bout.FightStartTick >= FightTicks) bouts.Remove(bout);
        }
    }
}
