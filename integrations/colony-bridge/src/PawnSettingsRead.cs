// Extracted from PawnConfigTool.cs; see ../PROVENANCE.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class PawnSettingsRead
    {
        // ==================================================================
        // work{}
        // ==================================================================

        /// <summary>
        /// The private `DefMap&lt;WorkTypeDef,int&gt; priorities`. Null if
        /// RimWorld ever renames it, and every caller reports the null rather
        /// than emitting a stored priority of 0 that would read as "never do
        /// this".
        /// </summary>
        private static readonly FieldInfo PrioritiesField =
            BridgeCommon.PrivateInstanceField(typeof(Pawn_WorkSettings), "priorities");

        /// <summary>
        /// Every work type in the order the Work tab lays its columns out:
        /// `WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder` is
        /// `AllDefs.OrderByDescending(naturalPriority)`, reproduced here so the
        /// enumeration is materialised once instead of per pawn.
        /// </summary>
        internal static List<WorkTypeDef> WorkTypesInTabOrder()
        {
            try
            {
                var all = DefDatabase<WorkTypeDef>.AllDefsListForReading;
                if (all == null)
                    return new List<WorkTypeDef>();
                return all.Where(w => w != null)
                          .OrderByDescending(w => w.naturalPriority)
                          .ToList();
            }
            catch { return new List<WorkTypeDef>(); }
        }

        /// <summary>Is the Work tab in numbered-priority mode? Null when the
        /// setting could not be read at all, which is not the same as "simple
        /// mode" and is never collapsed into it.</summary>
        internal static bool? ManualPriorities()
        {
            try
            {
                if (Current.Game == null || Current.Game.playSettings == null)
                    return null;
                return Current.Game.playSettings.useWorkPriorities;
            }
            catch { return null; }
        }

        /// <summary>The raw cell of the private priority table, unmasked by
        /// simple mode. Null when the table or the field is unreadable.</summary>
        internal static int? StoredPriority(Pawn_WorkSettings settings, WorkTypeDef def)
        {
            if (settings == null || def == null || PrioritiesField == null)
                return null;
            try
            {
                var map = PrioritiesField.GetValue(settings) as DefMap<WorkTypeDef, int>;
                if (map == null)
                    return null;
                return map[def];
            }
            catch { return null; }
        }

        /// <summary>
        /// The Work tab for one pawn. **Never null**, on the same contract as
        /// `bio{}`: a pawn with no work settings gets a block saying
        /// `applies: false`, because an absent work block read as "no jobs
        /// assigned" is the Lucas bug wearing a work tab.
        /// </summary>
        internal static Dictionary<string, object> WorkBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object>();

            Pawn_WorkSettings settings = null;
            try { settings = pawn.workSettings; }
            catch { settings = null; }

            var everWork = settings != null && BridgeCommon.Try(() => settings.EverWork, false);
            var initialized = settings != null && BridgeCommon.Try(() => settings.Initialized, false);

            block["applies"] = everWork;
            block["hasWorkSettings"] = settings != null;
            block["everWork"] = everWork;
            block["initialized"] = initialized;

            var manual = ManualPriorities();
            block["manualPriorities"] = manual;
            block["priorityMin"] = 0;
            block["priorityMax"] = 4;

            var rows = new List<object>();
            var active = new List<object>();
            var disabledNames = new List<object>();
            var storedReadable = true;

            foreach (var def in WorkTypesInTabOrder())
            {
                var row = new Dictionary<string, object>();
                row["name"] = BridgeCommon.SafeString(() => def.defName);
                row["label"] = BridgeCommon.SafeString(() => def.labelShort) ??
                               BridgeCommon.SafeString(() => def.label);
                row["naturalPriority"] = BridgeCommon.Try(() => def.naturalPriority, 0);
                row["visibleInTab"] = BridgeCommon.Try(() => def.visible, true);

                var disabled = BridgeCommon.Try(() => pawn.WorkTypeIsDisabled(def), false);
                row["disabled"] = disabled;
                if (disabled)
                    disabledNames.Add(row["name"]);

                // GetPriority is only safe once EverWork is true; see the class
                // remarks. Null here means NOT READ, never 0.
                int? effective = null;
                if (everWork)
                    effective = BridgeCommon.TryN(() => settings.GetPriority(def));
                var stored = everWork ? StoredPriority(settings, def) : null;
                if (everWork && stored == null)
                    storedReadable = false;

                row["priority"] = effective;
                row["priorityStored"] = stored;
                row["active"] = effective != null && effective.Value > 0;
                if (effective != null && effective.Value > 0 && !disabled)
                    active.Add(row["name"]);
                rows.Add(row);
            }

            block["types"] = rows;
            block["typeCount"] = rows.Count;
            block["activeWorkTypes"] = active;
            block["disabledWorkTypes"] = disabledNames;
            block["priorityStoredReadable"] = !everWork || storedReadable;
            block["note"] = everWork
                ? (manual == false
                    ? "The Work tab is in SIMPLE (checkbox) mode: Pawn_WorkSettings.GetPriority returns 3 for every active job whatever is stored, so `priority` is 3-or-0 here and `priorityStored` is the real cell. A write of any priority above 0 becomes 3, which is what clicking the checkbox does."
                    : "Numbered priorities are on. priority == priorityStored; 0 means never, 1 is most urgent, 4 is least.")
                : (settings == null
                    ? "This pawn has no work settings at all (animals, mechanoids, other factions). Not read, not empty."
                    : "Pawn_WorkSettings.EverWork is false: this pawn has never had a priority table. Reading one would have called Log.Error AND assigned it six jobs, so nothing was read. Not empty — not looked at.");
            return block;
        }

        // ==================================================================
        // schedule{}
        // ==================================================================

        private static readonly object LetterGate = new object();
        private static Dictionary<string, TimeAssignmentDef> _byLetter;
        private static Dictionary<TimeAssignmentDef, string> _letterOf;

        /// <summary>
        /// One letter per TimeAssignmentDef, assigned deterministically from the
        /// def names so a 24-character schedule string means the same thing on
        /// every call and on both sides of a write. First choice is the def's
        /// initial (Anything -> A, Work -> W, Joy -> J, Sleep -> S, Meditate ->
        /// M); a collision walks the rest of the name, then the alphabet. The key
        /// is returned in every reply, so a caller never has to know this rule.
        /// </summary>
        private static void EnsureLetters()
        {
            lock (LetterGate)
            {
                if (_byLetter != null)
                    return;

                var byLetter = new Dictionary<string, TimeAssignmentDef>(StringComparer.OrdinalIgnoreCase);
                var letterOf = new Dictionary<TimeAssignmentDef, string>();

                List<TimeAssignmentDef> defs;
                try
                {
                    var all = DefDatabase<TimeAssignmentDef>.AllDefsListForReading;
                    defs = all != null ? all.Where(d => d != null).ToList() : new List<TimeAssignmentDef>();
                }
                catch { defs = new List<TimeAssignmentDef>(); }

                foreach (var def in defs)
                {
                    var name = BridgeCommon.SafeString(() => def.defName) ?? "?";
                    string chosen = null;
                    foreach (var c in name.ToUpperInvariant())
                    {
                        if (c < 'A' || c > 'Z')
                            continue;
                        var s = c.ToString();
                        if (!byLetter.ContainsKey(s))
                        {
                            chosen = s;
                            break;
                        }
                    }
                    if (chosen == null)
                    {
                        for (var c = 'A'; c <= 'Z'; c++)
                        {
                            var s = c.ToString();
                            if (!byLetter.ContainsKey(s))
                            {
                                chosen = s;
                                break;
                            }
                        }
                    }
                    if (chosen == null)
                        continue;
                    byLetter[chosen] = def;
                    letterOf[def] = chosen;
                }

                _byLetter = byLetter;
                _letterOf = letterOf;
            }
        }

        internal static string LetterFor(TimeAssignmentDef def)
        {
            if (def == null)
                return "?";
            EnsureLetters();
            string s;
            return _letterOf.TryGetValue(def, out s) ? s : "?";
        }

        internal static TimeAssignmentDef AssignmentForLetter(string letter)
        {
            if (string.IsNullOrEmpty(letter))
                return null;
            EnsureLetters();
            TimeAssignmentDef def;
            return _byLetter.TryGetValue(letter, out def) ? def : null;
        }

        /// <summary>letter -> defName, emitted with every schedule so the
        /// 24-character string is self-describing.</summary>
        internal static Dictionary<string, object> LetterKey()
        {
            EnsureLetters();
            var key = new Dictionary<string, object>();
            foreach (var pair in _byLetter)
                key[pair.Key] = BridgeCommon.SafeString(() => pair.Value.defName);
            return key;
        }

        /// <summary>
        /// The Assign tab's 24 hours as one string, hour 0 first. **Never
        /// null**: a pawn with no timetable says `applies: false` rather than
        /// being absent.
        /// </summary>
        internal static Dictionary<string, object> ScheduleBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object>();

            Pawn_TimetableTracker timetable = null;
            try { timetable = pawn.timetable; }
            catch { timetable = null; }

            block["applies"] = timetable != null;
            block["hasTimetable"] = timetable != null;
            block["key"] = LetterKey();

            List<TimeAssignmentDef> times = null;
            if (timetable != null)
            {
                try { times = timetable.times != null ? timetable.times.ToList() : null; }
                catch { times = null; }
            }

            block["hourCount"] = times == null ? (object)null : times.Count;

            if (times == null)
            {
                block["hours"] = null;
                block["assignments"] = new List<object>();
                block["current"] = null;
                block["currentHour"] = null;
                block["note"] = timetable == null
                    ? "This pawn has no timetable tracker (animals, other factions). Not read, not empty."
                    : "Pawn_TimetableTracker.times was not readable. Nothing here is a schedule.";
                return block;
            }

            var letters = new System.Text.StringBuilder(times.Count);
            var names = new List<object>();
            foreach (var t in times)
            {
                letters.Append(LetterFor(t));
                names.Add(t != null ? BridgeCommon.SafeString(() => t.defName) : null);
            }
            block["hours"] = letters.ToString();
            block["assignments"] = names;

            var current = BridgeCommon.Try<TimeAssignmentDef>(() => timetable.CurrentAssignment, null);
            block["current"] = current != null ? BridgeCommon.SafeString(() => current.defName) : null;
            block["currentLetter"] = current != null ? LetterFor(current) : null;
            block["currentHour"] = BridgeCommon.TryN(() => GenLocalDate.HourOfDay(pawn));
            block["note"] = times.Count == 24
                ? "hours[] is one letter per hour, hour 0 first; key{} maps every letter to its TimeAssignmentDef. current is what the pawn is assigned RIGHT NOW, which is a prisoner-and-colonist-aware read and can differ from the letter at currentHour."
                : "This timetable does not hold 24 hours (" + times.Count + "). Reported as found; a schedule write refuses on it rather than reshaping it.";
            return block;
        }

        // ==================================================================
        // settings{}
        // ==================================================================

        /// <summary>
        /// The Assign tab's row for one pawn: medical care, hostility response,
        /// self-tend, the two follow toggles, the allowed area and the master.
        /// **Never null.**
        /// </summary>
        internal static Dictionary<string, object> SettingsBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object>();

            block["thingId"] = BridgeCommon.SafeString(() => pawn.ThingID);

            Pawn_PlayerSettings ps = null;
            try { ps = pawn.playerSettings; }
            catch { ps = null; }

            block["applies"] = ps != null;
            block["hasPlayerSettings"] = ps != null;

            if (ps == null)
            {
                block["medCare"] = null;
                block["hostilityResponse"] = null;
                block["selfTend"] = null;
                block["followDrafted"] = null;
                block["followFieldwork"] = null;
                block["allowedArea"] = null;
                block["allowedAreaIsUnrestricted"] = null;
                block["supportsAllowedAreas"] = null;
                block["master"] = null;
                block["masterThingId"] = null;
                block["note"] = "This pawn has no playerSettings at all (wild animals, other factions). Every field above is NOT READ, not a default.";
                return block;
            }

            block["medCare"] = BridgeCommon.SafeString(() => ps.medCare.ToString());
            block["hostilityResponse"] = BridgeCommon.SafeString(() => ps.hostilityResponse.ToString());
            block["selfTend"] = BridgeCommon.TryN(() => ps.selfTend);
            block["followDrafted"] = BridgeCommon.TryN(() => ps.followDrafted);
            block["followFieldwork"] = BridgeCommon.TryN(() => ps.followFieldwork);

            // The plain getter, not EffectiveAreaRestrictionInPawnCurrentMap:
            // the "effective" one routes through Faction.OfPlayer, which pauses
            // the colony on a map with no player faction. See the class remarks.
            var area = BridgeCommon.Try<Area>(() => ps.AreaRestrictionInPawnCurrentMap, null);
            block["allowedArea"] = area != null ? BridgeCommon.SafeString(() => area.Label) : null;
            block["allowedAreaIsUnrestricted"] = area == null;
            block["allowedAreaCellCount"] = area != null ? BridgeCommon.TryN(() => area.TrueCount) : null;
            block["supportsAllowedAreas"] = BridgeCommon.TryN(() => ps.SupportsAllowedAreas);

            var master = BridgeCommon.Try<Pawn>(() => ps.Master, null);
            block["master"] = master != null ? BridgeCommon.SafeString(() => master.LabelShortCap.ToString()) : null;
            block["masterThingId"] = master != null ? BridgeCommon.SafeString(() => master.ThingID) : null;
            block["note"] = "allowedArea null means UNRESTRICTED — the whole map — which is a real setting, not a missing read; allowedAreaIsUnrestricted says so as a bool. selfTend is OFF by default for every colonist including new joiners.";
            return block;
        }

        /// <summary>
        /// What a `home/pawn_config` write may be handed, computed once per call
        /// rather than repeated inside every pawn row: the areas on this map that
        /// can be assigned, and the two enums spelled out.
        /// </summary>
        internal static Dictionary<string, object> Options(Map map)
        {
            var options = new Dictionary<string, object>();

            var areas = new List<object>();
            try
            {
                var manager = map != null ? map.areaManager : null;
                var all = manager != null ? manager.AllAreas : null;
                if (all != null)
                {
                    for (var i = 0; i < all.Count; i++)
                    {
                        var a = all[i];
                        if (a == null)
                            continue;
                        var row = new Dictionary<string, object>();
                        row["label"] = BridgeCommon.SafeString(() => a.Label);
                        row["assignable"] = BridgeCommon.Try(() => a.AssignableAsAllowed(), false);
                        row["cellCount"] = BridgeCommon.TryN(() => a.TrueCount);
                        areas.Add(row);
                    }
                }
            }
            catch { }
            options["allowedAreas"] = areas;
            options["allowedAreaUnrestricted"] = "Pass allowedArea=\"none\" (or \"unrestricted\") to clear the restriction. Omitting it leaves the setting alone.";
            options["medCare"] = Enum.GetNames(typeof(MedicalCareCategory)).Cast<object>().ToList();
            options["hostilityResponse"] = Enum.GetNames(typeof(HostilityResponseMode)).Cast<object>().ToList();
            options["scheduleKey"] = LetterKey();
            return options;
        }

        // ==================================================================
        // animals{} -- the Animals tab, read
        // ==================================================================

        /// <summary>
        /// The player faction, or null. NOT `Faction.OfPlayer`: its body is
        /// `get_OfPlayerSilentFail` followed by `Verse.Log.Error`, and
        /// `Log.Error` calls `TickManager.Pause()`. Every "is this one of ours"
        /// test below goes through this.
        /// </summary>
        internal static Faction PlayerFactionSilent()
        {
            try { return Faction.OfPlayerSilentFail; }
            catch { return null; }
        }

        /// <summary>
        /// `Pawn_TrainingTracker.GetSteps(TrainableDef)` is **internal**, so an
        /// external assembly cannot call it. The Animals tab draws
        /// "steps / td.steps" with it (TrainingCardUtility), and a training row
        /// without its progress is the difference between "started" and "nearly
        /// done". It is reached by reflection, and when the lookup fails the
        /// reply says `stepsReadable: false` and every `steps` is null -- never
        /// 0, which would read as "no progress at all".
        /// </summary>
        private static readonly MethodInfo TrainingGetSteps = FindTrainingGetSteps();

        private static MethodInfo FindTrainingGetSteps()
        {
            try
            {
                return typeof(Pawn_TrainingTracker).GetMethod(
                    "GetSteps",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(TrainableDef) },
                    null);
            }
            catch { return null; }
        }

        /// <summary>`CompEggLayer.eggProgress` is private and `CanLayNow` only
        /// says "full". Same contract as the steps above: null and a readable
        /// flag, never a 0 that means "not looked at".</summary>
        private static readonly FieldInfo EggProgressField =
            BridgeCommon.PrivateInstanceField(typeof(CompEggLayer), "eggProgress");

        /// <summary>
        /// The trainables in the order the Animals tab draws them.
        /// `TrainableUtility.TrainableDefsInListOrder` is the game's own sorted
        /// list (by `listPriority` descending, prerequisites indented) and is
        /// what `NextTrainableToTrain` walks, so a caller reading this list top
        /// to bottom sees what the game will train next. It hands back the live
        /// static list, so it is COPIED. DefDatabase order is the fallback, and
        /// is not the same order.
        /// </summary>
        internal static List<TrainableDef> TrainableDefsInOrder()
        {
            try
            {
                var ordered = TrainableUtility.TrainableDefsInListOrder;
                if (ordered != null && ordered.Count > 0)
                    return ordered.ToList();
            }
            catch { }
            try { return DefDatabase<TrainableDef>.AllDefsListForReading.ToList(); }
            catch { return new List<TrainableDef>(); }
        }

        /// <summary>
        /// The four designations the Animals tab and its designators put on a
        /// pawn. Named here once so the read and `home/pawn_config`'s write
        /// cannot disagree about which they are.
        /// </summary>
        internal static List<KeyValuePair<string, DesignationDef>> AnimalDesignationDefs()
        {
            var list = new List<KeyValuePair<string, DesignationDef>>();
            AddDesignationDef(list, "Slaughter", BridgeCommon.Try<DesignationDef>(() => DesignationDefOf.Slaughter, null));
            AddDesignationDef(list, "ReleaseAnimalToWild", BridgeCommon.Try<DesignationDef>(() => DesignationDefOf.ReleaseAnimalToWild, null));
            AddDesignationDef(list, "Tame", BridgeCommon.Try<DesignationDef>(() => DesignationDefOf.Tame, null));
            AddDesignationDef(list, "Hunt", BridgeCommon.Try<DesignationDef>(() => DesignationDefOf.Hunt, null));
            return list;
        }

        private static void AddDesignationDef(List<KeyValuePair<string, DesignationDef>> list, string name, DesignationDef def)
        {
            if (def != null)
                list.Add(new KeyValuePair<string, DesignationDef>(name, def));
        }

        /// <summary>The payload key each of those four designations is reported
        /// under, on the read and in a write's field rows.</summary>
        internal static string DesignationKey(string defName)
        {
            if (string.Equals(defName, "ReleaseAnimalToWild", StringComparison.Ordinal))
                return "releaseToWild";
            if (string.IsNullOrEmpty(defName))
                return defName;
            return char.ToLowerInvariant(defName[0]) + defName.Substring(1);
        }

        /// <summary>
        /// The designation of one def on one pawn, or null.
        ///
        /// **Not `DesignationManager.DesignationOn(t, def)`**, deliberately: its
        /// first branch is `if (def.targetType == TargetType.Cell) Log.Error(...)`,
        /// and `Log.Error` pauses the colony. A mod that re-declares one of these
        /// defs as cell-targeted would turn a read into a pause. Walking
        /// `AllDesignationsOn(t)` -- which returns the live indexed list, or a
        /// shared empty one -- asks the same question with no error arm at all.
        /// The list is READ ONLY here; `RemoveDesignation` mutates it, so the
        /// write side takes the reference first and removes afterwards.
        /// </summary>
        internal static Designation DesignationOnSafe(Pawn pawn, DesignationDef def)
        {
            if (pawn == null || def == null)
                return null;
            try
            {
                var map = pawn.MapHeld;
                var manager = map != null ? map.designationManager : null;
                if (manager == null)
                    return null;
                var all = manager.AllDesignationsOn(pawn);
                if (all == null)
                    return null;
                for (var i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    if (d != null && d.def == def)
                        return d;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Everything the Animals tab shows about one animal: what it is, how
        /// wild, who it is bonded to, every trainable with its progress, the
        /// designations standing on it and what it is producing.
        ///
        /// **Never null**, and never absent. A humanlike gets a block that says
        /// `applies: false` and `isAnimal: false` rather than no key at all --
        /// the same contract `work{}` and `settings{}` take, and for the same
        /// reason: an absent key and an old build read identically to a caller.
        ///
        /// **What is deliberately NOT here**, because `settings{}` already has
        /// it and two copies drift: `master`, `masterThingId`, `followDrafted`,
        /// `followFieldwork`, `allowedArea`. `settingsBlockCarries` says so in
        /// the payload. The hunger level is in `needs{}` for the same reason.
        ///
        /// ## Hazards, IL-scanned against Assembly-CSharp 1.6.9676.17735
        ///
        ///   * `Pawn_TrainingTracker.GetWanted` / `.HasLearned` /
        ///     `.CanBeTrained` / `.NextTrainableToTrain` / `.CanAssignToTrain`
        ///     read `DefMap` cells and def fields and store nothing.
        ///     `HasLearned` and `CanAssignToTrain` both route through
        ///     `TrainableUtility.GetTrainability`, whose Odyssey branch reads a
        ///     hediff set and sorts a static def cache once. No writes.
        ///   * **`Pawn.GetStatValue(StatDefOf.Wildness)` is only read for
        ///     ANIMALS.** `StatWorker.GetValueUnfinalized` opens with a
        ///     `Log.ErrorOnce` for a stat that is disabled on the subject --
        ///     guarded by `Prefs.DevMode`, which is a setting a player can have
        ///     on, so the guard is not enough to rely on. Wildness is never
        ///     disabled on an animal. `StatWorker.GetValue` also fills
        ///     `temporaryStatCache`; that is a memo behind a tick stamp, the same
        ///     class as `HediffSet.PainTotal`.
        ///   * `TrainableUtility.GetAllColonistBondsFor` filters
        ///     `relations.DirectRelations` -- a plain field read -- and touches
        ///     no opinion path, so it is nowhere near
        ///     `Pawn_RelationsTracker.OpinionOf`'s memory deletion.
        ///   * `CompHasGatherableBodyResource.Fullness` and `.ActiveAndFull` are
        ///     field reads. `.Active` is **protected**, so "is it producing right
        ///     now" is not readable from outside the assembly at all; that is
        ///     said in `activeIsNotReadable` rather than guessed at.
        ///   * `CompEggLayer.CanLayNow` reads `Pawn.Sterile()` (hediffs and
        ///     genes) and `ageTracker.CurLifeStage`. Both pure.
        ///   * `Hediff_Pregnant.GestationProgress` is `Severity`, a field.
        /// </summary>
        internal static Dictionary<string, object> AnimalBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object>();

            var isAnimal = BridgeCommon.Try(() => pawn.RaceProps != null && pawn.RaceProps.Animal, false);
            block["isAnimal"] = isAnimal;
            block["applies"] = isAnimal;

            if (!isAnimal)
            {
                block["note"] = "This pawn is not an animal (RaceProps.Animal is false), so the Animals tab has no row for it. "
                              + "Every animal field is NOT APPLICABLE here, not unread. Humanlikes and mechanoids land here.";
                return block;
            }

            var race = BridgeCommon.Try<RaceProperties>(() => pawn.RaceProps, null);
            var player = PlayerFactionSilent();
            var faction = BridgeCommon.Try<Faction>(() => pawn.Faction, null);

            // tame / wild are NOT complements: a visiting trader's muffalo is
            // neither ours nor wild, and calling it either would be a lie.
            block["tame"] = player != null && faction != null && faction == player;
            block["wild"] = faction == null;
            block["otherFaction"] = faction != null && (player == null || faction != player);
            block["faction"] = faction != null ? BridgeCommon.SafeString(() => faction.Name) : null;

            block["race"] = BridgeCommon.SafeString(() => pawn.def != null ? pawn.def.defName : null);
            block["raceLabel"] = BridgeCommon.SafeString(() => pawn.def != null ? pawn.def.label : null);
            block["kindLabel"] = BridgeCommon.SafeString(() => pawn.KindLabel);

            // `name` is the GIVEN name and is null for an unnamed animal, which
            // is a real state, not a failed read: `named` says which.
            var hasName = BridgeCommon.Try(() => pawn.Name != null, false);
            block["named"] = hasName;
            block["name"] = hasName ? BridgeCommon.SafeString(() => pawn.Name.ToStringShort) : null;
            block["label"] = BridgeCommon.SafeString(() => pawn.LabelShortCap.ToString());
            // Animals share a race name -- five muffalo are all "Muffalo" -- so
            // a name is not an address for one. This is, and it is the same
            // field settings{} carries for a colonist.
            block["thingId"] = BridgeCommon.SafeString(() => pawn.ThingID);

            block["gender"] = BridgeCommon.SafeString(() => pawn.gender.ToString());
            block["ageYears"] = BridgeCommon.TryN(() => pawn.ageTracker.AgeBiologicalYears);
            block["ageYearsFloat"] = Round3(BridgeCommon.TryN(() => pawn.ageTracker.AgeBiologicalYearsFloat));
            block["lifeStage"] = BridgeCommon.SafeString(
                () => pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.defName : null);

            block["bodySize"] = Round3(BridgeCommon.TryN(() => pawn.BodySize));
            block["baseBodySize"] = Round3(race != null ? BridgeCommon.TryN(() => race.baseBodySize) : null);
            block["baseHungerRate"] = Round3(race != null ? BridgeCommon.TryN(() => race.baseHungerRate) : null);
            block["petness"] = Round3(race != null ? BridgeCommon.TryN(() => race.petness) : null);
            block["predator"] = race != null && BridgeCommon.Try(() => race.predator, false);
            block["packAnimal"] = race != null && BridgeCommon.Try(() => race.packAnimal, false);
            block["canReleaseToWild"] = race != null && BridgeCommon.Try(() => race.canReleaseToWild, false);

            // Wildness is a STAT in 1.6, not a RaceProperties field -- there is
            // no RaceProps.wildness. Only read for animals; see the remarks.
            var wildness = BridgeCommon.TryN(() => pawn.GetStatValue(StatDefOf.Wildness));
            block["wildness"] = Round3(wildness);
            block["wildnessRead"] = wildness != null;

            var trainability = BridgeCommon.Try<TrainabilityDef>(() => TrainableUtility.GetTrainability(pawn), null);
            block["trainability"] = trainability != null
                ? BridgeCommon.SafeString(() => trainability.defName)
                : null;
            block["trainabilityFromRace"] = race != null && race.trainability != null
                ? BridgeCommon.SafeString(() => race.trainability.defName)
                : null;
            block["minimumHandlingSkill"] = BridgeCommon.TryN(() => TrainableUtility.MinimumHandlingSkill(pawn));

            // Bonds: the colonists this animal is BONDED to, which is a
            // different relationship from having a master and is the one the
            // slaughter warning is about.
            var bonded = new List<object>();
            try
            {
                var bonds = TrainableUtility.GetAllColonistBondsFor(pawn);
                if (bonds != null)
                {
                    foreach (var b in bonds)
                    {
                        if (b == null)
                            continue;
                        bonded.Add(BridgeCommon.SafeString(() => b.LabelShortCap.ToString()));
                    }
                }
            }
            catch { }
            block["bonded"] = bonded;
            block["bondedCount"] = bonded.Count;

            block["training"] = TrainingSubBlock(pawn);
            block["designations"] = DesignationsSubBlock(pawn);
            block["produce"] = ProduceSubBlock(pawn);

            block["settingsBlockCarries"] =
                "master, masterThingId, followDrafted, followFieldwork and allowedArea are NOT repeated here -- they are in "
                + "settings{} (pass settings:true), from the same reader. The hunger level is in needs{} (pass needs:true). "
                + "Two copies of one field is how two answers start disagreeing.";
            return block;
        }

        /// <summary>
        /// Every trainable in the Animals tab's own order, with what the tab
        /// draws in the cell: whether the player has ticked it (`wanted`),
        /// whether the animal has it (`learned`), whether it can be ticked at
        /// all (`canTrain`, with the game's own `reason` when it cannot) and how
        /// far along it is (`steps`).
        ///
        /// Four different questions live in this row and they are NOT the same:
        ///
        ///   * `canTrain` -- `CanAssignToTrain(td, out visible)`. Can this
        ///     animal EVER learn it: body size, trainability, race tags. The
        ///     `reason` is RimWorld's own translated sentence
        ///     ("CannotTrainNotSmartEnough"), not one written here.
        ///   * `visible` -- the same call's out parameter. False means the game
        ///     does not draw the column at all for this race (an untrainable
        ///     tag, or a DLC-only special trainable on a race without it). A row
        ///     with `visible:false` is not a refusal, it is not a thing.
        ///   * `learned` -- `HasLearned(td)`. Already trained.
        ///   * `canBeTrainedNow` -- `CanBeTrained(td)`. There are steps left AND
        ///     every prerequisite is finished. This is what
        ///     `NextTrainableToTrain` walks, so a `wanted` trainable with
        ///     `canBeTrainedNow:false` is the reason a handler is not working.
        /// </summary>
        internal static Dictionary<string, object> TrainingSubBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object>();

            Pawn_TrainingTracker training = null;
            try { training = pawn.training; }
            catch { training = null; }

            block["applies"] = training != null;
            block["stepsReadable"] = TrainingGetSteps != null;
            if (TrainingGetSteps == null)
            {
                block["stepsNote"] = "Pawn_TrainingTracker.GetSteps(TrainableDef) is internal to Assembly-CSharp and was not found by "
                                   + "reflection in this build, so every steps/stepsDone below is null. Null means NOT READ, not zero.";
            }

            if (training == null)
            {
                block["trainables"] = new List<object>();
                block["trainableCount"] = 0;
                block["nextToTrain"] = null;
                block["wantedCount"] = 0;
                block["learnedCount"] = 0;
                block["note"] = "This pawn has no training tracker at all. Every field is NOT APPLICABLE, not unread.";
                return block;
            }

            var rows = new List<object>();
            var wantedCount = 0;
            var learnedCount = 0;
            foreach (var td in TrainableDefsInOrder())
            {
                if (td == null)
                    continue;

                var row = new Dictionary<string, object>();
                row["name"] = BridgeCommon.SafeString(() => td.defName);
                row["label"] = BridgeCommon.SafeString(() => td.LabelCap.ToString());
                row["indent"] = BridgeCommon.TryN(() => td.indent);

                var wanted = BridgeCommon.Try(() => training.GetWanted(td), false);
                var learned = BridgeCommon.Try(() => training.HasLearned(td), false);
                row["wanted"] = wanted;
                row["learned"] = learned;
                if (wanted) wantedCount++;
                if (learned) learnedCount++;

                var visible = false;
                string reason = null;
                var canTrain = false;
                try
                {
                    bool vis;
                    var report = training.CanAssignToTrain(td, out vis);
                    visible = vis;
                    canTrain = report.Accepted;
                    if (!report.Accepted)
                    {
                        var text = report.Reason;
                        reason = string.IsNullOrEmpty(text)
                            ? (vis
                                ? "RimWorld returned a rejection with no reason text."
                                : "Not shown for this race at all (an untrainable tag, or a DLC-only special trainable).")
                            : text;
                    }
                }
                catch
                {
                    canTrain = false;
                    visible = false;
                    reason = "Pawn_TrainingTracker.CanAssignToTrain threw; treat canTrain as NOT READ.";
                }
                row["canTrain"] = canTrain;
                row["visible"] = visible;
                row["reason"] = reason;

                row["canBeTrainedNow"] = BridgeCommon.Try(() => training.CanBeTrained(td), false);

                var total = BridgeCommon.TryN(() => td.steps);
                int? done = null;
                if (TrainingGetSteps != null)
                {
                    try { done = (int)TrainingGetSteps.Invoke(training, new object[] { td }); }
                    catch { done = null; }
                }
                row["stepsDone"] = done;
                row["stepsTotal"] = total;
                row["steps"] = done != null && total != null
                    ? done.Value + "/" + total.Value
                    : null;

                row["requiredTrainability"] = td.requiredTrainability != null
                    ? BridgeCommon.SafeString(() => td.requiredTrainability.defName)
                    : null;
                row["minBodySize"] = Round3(BridgeCommon.TryN(() => td.minBodySize));

                rows.Add(row);
            }

            block["trainables"] = rows;
            block["trainableCount"] = rows.Count;
            block["wantedCount"] = wantedCount;
            block["learnedCount"] = learnedCount;

            var next = BridgeCommon.Try<TrainableDef>(() => training.NextTrainableToTrain(), null);
            block["nextToTrain"] = next != null ? BridgeCommon.SafeString(() => next.defName) : null;
            block["note"] = "Rows are in TrainableUtility.TrainableDefsInListOrder -- the order the Animals tab draws and the order "
                          + "NextTrainableToTrain walks. wanted is the tick box; learned is HasLearned; canTrain is CanAssignToTrain "
                          + "with RimWorld's own reason when it says no; canBeTrainedNow is CanBeTrained (steps left AND prerequisites "
                          + "done). visible:false means the game draws no column for this race, which is not the same as a refusal.";
            return block;
        }

        /// <summary>
        /// The designations standing on this pawn. `slaughter`, `tame`, `hunt`
        /// and `releaseToWild` are hoisted as plain bools -- "marked for
        /// slaughter" should be one field, not a list a caller has to search --
        /// and `all[]` carries every designation on the pawn including any this
        /// list does not name, so nothing is hidden by being unlisted.
        /// </summary>
        internal static Dictionary<string, object> DesignationsSubBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object>();

            Map map = null;
            try { map = pawn.MapHeld; }
            catch { map = null; }
            var manager = map != null ? BridgeCommon.Try<DesignationManager>(() => map.designationManager, null) : null;

            block["readable"] = manager != null;
            if (manager == null)
            {
                block["all"] = new List<object>();
                block["count"] = 0;
                block["slaughter"] = null;
                block["releaseToWild"] = null;
                block["tame"] = null;
                block["hunt"] = null;
                block["note"] = "No map designation manager was reachable for this pawn (MapHeld was null -- carried, or in a caravan). "
                              + "Every flag here is NULL meaning NOT READ, never false meaning 'not marked'.";
                return block;
            }

            var all = new List<object>();
            try
            {
                var list = manager.AllDesignationsOn(pawn);
                if (list != null)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var d = list[i];
                        if (d == null || d.def == null)
                            continue;
                        all.Add(BridgeCommon.SafeString(() => d.def.defName));
                    }
                }
            }
            catch { }
            block["all"] = all;
            block["count"] = all.Count;

            foreach (var pair in AnimalDesignationDefs())
                block[DesignationKey(pair.Key)] = DesignationOnSafe(pawn, pair.Value) != null;

            // A DefOf a mod removed would leave the key missing; emitting false
            // there would say "not marked" about something never looked at.
            foreach (var key in new[] { "slaughter", "releaseToWild", "tame", "hunt" })
            {
                if (!block.ContainsKey(key))
                    block[key] = null;
            }

            block["note"] = "slaughter / releaseToWild / tame / hunt are the four the Animals tab draws; all[] is EVERY designation on "
                          + "the pawn, so one this list does not name is still visible. A null flag means the DesignationDef itself was "
                          + "not found in this build -- not that the pawn is unmarked.";
            return block;
        }

        /// <summary>
        /// Eggs, milk, wool and pregnancy. Every fullness is 0..1.
        ///
        /// `CompHasGatherableBodyResource.Active` -- "is it producing at all
        /// right now", which folds in gender, life stage and shambler state --
        /// is `protected`, so it cannot be read from outside Assembly-CSharp.
        /// That is stated in `activeIsNotReadable` rather than approximated: a
        /// full udder on a comp that is not Active yields nothing, and guessing
        /// which is the kind of confident wrong answer this companion exists to
        /// stop. `activeAndFull` IS public and is emitted.
        /// </summary>
        internal static Dictionary<string, object> ProduceSubBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object>();
            var comps = pawn as ThingWithComps;

            var egg = comps != null ? BridgeCommon.Try<CompEggLayer>(() => comps.GetComp<CompEggLayer>(), null) : null;
            block["hasEggLayer"] = egg != null;
            block["eggFullness"] = null;
            block["eggFullnessReadable"] = false;
            block["eggCanLayNow"] = null;
            block["eggFullyFertilized"] = null;
            if (egg != null)
            {
                if (EggProgressField != null)
                {
                    var progress = BridgeCommon.TryN(() => (float)EggProgressField.GetValue(egg));
                    block["eggFullness"] = Round3(progress);
                    block["eggFullnessReadable"] = progress != null;
                }
                block["eggCanLayNow"] = BridgeCommon.TryN(() => egg.CanLayNow);
                block["eggFullyFertilized"] = BridgeCommon.TryN(() => egg.FullyFertilized);
            }

            var milk = comps != null ? BridgeCommon.Try<CompMilkable>(() => comps.GetComp<CompMilkable>(), null) : null;
            block["hasMilkable"] = milk != null;
            block["milkFullness"] = milk != null ? Round3(BridgeCommon.TryN(() => milk.Fullness)) : null;
            block["milkFull"] = milk != null ? BridgeCommon.TryN(() => milk.ActiveAndFull) : null;

            var wool = comps != null ? BridgeCommon.Try<CompShearable>(() => comps.GetComp<CompShearable>(), null) : null;
            block["hasShearable"] = wool != null;
            block["woolFullness"] = wool != null ? Round3(BridgeCommon.TryN(() => wool.Fullness)) : null;
            block["woolFull"] = wool != null ? BridgeCommon.TryN(() => wool.ActiveAndFull) : null;

            var pregnant = false;
            double? gestation = null;
            var hediffsRead = false;
            try
            {
                var set = pawn.health != null ? pawn.health.hediffSet : null;
                var hediffs = set != null ? set.hediffs : null;
                if (hediffs != null)
                {
                    hediffsRead = true;
                    for (var i = 0; i < hediffs.Count; i++)
                    {
                        var hp = hediffs[i] as Hediff_Pregnant;
                        if (hp == null)
                            continue;
                        pregnant = true;
                        gestation = RoundD(BridgeCommon.TryN(() => hp.GestationProgress));
                        break;
                    }
                }
            }
            catch { }
            block["pregnant"] = hediffsRead ? (object)pregnant : null;
            block["pregnancyReadable"] = hediffsRead;
            block["gestationProgress"] = gestation;
            block["gestationPeriodDays"] = Round3(BridgeCommon.TryN(
                () => pawn.RaceProps != null ? pawn.RaceProps.gestationPeriodDays : -1f));

            block["activeIsNotReadable"] =
                "CompHasGatherableBodyResource.Active is protected in Assembly-CSharp, so whether a comp is producing RIGHT NOW "
                + "(gender, life stage, shambler state) cannot be read from outside the game assembly. activeAndFull IS readable and "
                + "is milkFull / woolFull. A fullness of 1.0 with activeAndFull false means the comp is not active.";
            return block;
        }

        /// <summary>Three decimals, or null. A raw float serialises as
        /// 0.30000001192092896 and every number here is one somebody reads.</summary>
        private static double? RoundD(float? v)
        {
            if (v == null || float.IsNaN(v.Value) || float.IsInfinity(v.Value))
                return null;
            return Math.Round((double)v.Value, 3);
        }

        private static object Round3(float? v)
        {
            return RoundD(v);
        }

        // ==================================================================
        // relations{}
        // ==================================================================

        /// <summary>The private per-other-pawn social-thought cache. Read
        /// WITHOUT calling AppendSocialThoughts, which recalculates and can
        /// delete memories. See the class remarks.</summary>
        private static readonly FieldInfo SocialCacheField =
            BridgeCommon.PrivateInstanceField(typeof(SituationalThoughtHandler), "cachedSocialThoughts");

        /// <summary>The `activeThoughts` list inside a cache entry. The entry
        /// type is a private nested class; the list it holds is
        /// `List&lt;Thought_SituationalSocial&gt;`, which is public.</summary>
        private static readonly FieldInfo SocialActiveField = FindSocialActiveField();

        private static FieldInfo FindSocialActiveField()
        {
            try
            {
                var nested = typeof(SituationalThoughtHandler)
                    .GetNestedType("CachedSocialThoughts", BindingFlags.NonPublic | BindingFlags.Public);
                if (nested == null)
                    return null;
                return nested.GetField("activeThoughts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { return null; }
        }

        /// <summary>
        /// Family, partners, bonded animals and what this pawn thinks of every
        /// other living colonist. **Never null.**
        ///
        /// `colonists` is the already-walked list of living colonists on the map,
        /// passed in so the opinion pass is O(colonists) per pawn rather than a
        /// second traversal of `mapPawns`.
        /// </summary>
        internal static Dictionary<string, object> RelationsBlock(Pawn pawn, List<Pawn> colonists)
        {
            var block = new Dictionary<string, object>();

            Pawn_RelationsTracker tracker = null;
            try { tracker = pawn.relations; }
            catch { tracker = null; }

            block["applies"] = tracker != null;
            block["hasRelationsTracker"] = tracker != null;

            var direct = new List<object>();
            var partners = new List<object>();
            var bonded = new List<object>();

            if (tracker != null)
            {
                List<DirectPawnRelation> raw;
                try
                {
                    var list = tracker.DirectRelations;
                    raw = list != null ? list.ToList() : new List<DirectPawnRelation>();
                }
                catch { raw = new List<DirectPawnRelation>(); }

                foreach (var r in raw)
                {
                    if (r == null || r.def == null)
                        continue;
                    var other = r.otherPawn;
                    var row = new Dictionary<string, object>();
                    row["defName"] = BridgeCommon.SafeString(() => r.def.defName);
                    // The label the social card draws, gendered on the OTHER
                    // pawn: "wife", not "spouse"; "daughter", not "child".
                    row["label"] = other != null
                        ? BridgeCommon.SafeString(() => r.def.GetGenderSpecificLabelCap(other))
                        : BridgeCommon.SafeString(() => r.def.LabelCap);
                    row["otherName"] = other != null
                        ? BridgeCommon.SafeString(() => other.LabelShortCap.ToString())
                        : null;
                    row["otherThingId"] = other != null ? BridgeCommon.SafeString(() => other.ThingID) : null;
                    row["otherIsColonist"] = other != null && BridgeCommon.Try(() => other.IsColonist, false);
                    row["otherIsAnimal"] = other != null && BridgeCommon.Try(() => other.RaceProps != null && other.RaceProps.Animal, false);
                    row["otherDead"] = other == null ? (object)null : BridgeCommon.Try(() => other.Dead, false);
                    row["otherOnThisMap"] = other != null && BridgeCommon.Try(() => other.Spawned && other.Map == pawn.Map, false);
                    row["otherMissing"] = other == null;
                    row["startTicks"] = BridgeCommon.Try(() => r.startTicks, 0);
                    direct.Add(row);

                    var defName = row["defName"] as string;
                    if (defName == "Spouse" || defName == "Lover" || defName == "Fiance")
                        partners.Add(row);
                    if (defName == "Bond")
                        bonded.Add(row);
                }
            }

            block["direct"] = direct;
            block["directCount"] = direct.Count;
            // Lifted out because they are the two questions anybody asks of this
            // block: who is this colonist's partner, and which animal is bonded
            // to them. Both are rows from direct[], not a second read.
            block["partners"] = partners;
            block["bondedAnimals"] = bonded;

            // -------------------------------------------------------- opinions
            var opinions = new List<object>();
            var anyCached = false;
            if (tracker != null && colonists != null)
            {
                foreach (var other in colonists)
                {
                    if (other == null || other == pawn)
                        continue;
                    if (!BridgeCommon.Try(() => other.RaceProps != null && other.RaceProps.Humanlike, false))
                        continue;

                    bool cached;
                    var row = OpinionRow(pawn, other, out cached);
                    if (cached)
                        anyCached = true;
                    opinions.Add(row);
                }
                // Worst first. NOT BridgeCommon.Num: it unboxes a double and
                // every opinion here is an int, so that comparison would have
                // sorted every row as 0 and looked like it worked.
                try
                {
                    opinions = opinions
                        .Cast<Dictionary<string, object>>()
                        .OrderBy(r =>
                        {
                            object v;
                            if (!r.TryGetValue("opinion", out v) || v == null)
                                return 0.0;
                            try { return Convert.ToDouble(v); }
                            catch { return 0.0; }
                        })
                        .Cast<object>()
                        .ToList();
                }
                catch { }
            }

            block["colonistOpinions"] = opinions;
            block["colonistOpinionCount"] = opinions.Count;
            block["anySituationalSocialCached"] = anyCached;
            block["opinionMethod"] =
                "opinion is RECONSTRUCTED, not read from Pawn_RelationsTracker.OpinionOf. That call recalculates situational social thoughts, and Thought_Situational.Notify_BecameActive DELETES every memory of the def it produces (Notify_BecameInactive grants one) -- asking what two colonists think of each other would edit both their minds. The parts are: relation offsets from Pawn.GetRelations, social memories from memories.Memories grouped with Thought.GroupsWith, and situational social thoughts read out of the handler's own cache without recalculating. Each row carries situationalSocialCached: when it is false RimWorld has never filled that cache entry, the situational term is 0 by absence, and the opinion is a FLOOR rather than a total.";
            block["opinionScale"] = "-100..100, the same scale the social tab shows. Below -20 is a rival, above 20 a friend.";
            return block;
        }

        private static Dictionary<string, object> OpinionRow(Pawn pawn, Pawn other, out bool situationalCached)
        {
            situationalCached = false;

            var row = new Dictionary<string, object>();
            row["name"] = BridgeCommon.SafeString(() => other.LabelShortCap.ToString());
            row["thingId"] = BridgeCommon.SafeString(() => other.ThingID);

            // ---- relation offsets, and the labels the social card would draw
            var labels = new List<object>();
            double relationOffset = 0.0;
            try
            {
                foreach (var def in pawn.GetRelations(other))
                {
                    if (def == null)
                        continue;
                    relationOffset += BridgeCommon.Try(() => def.opinionOffset, 0);
                    var label = BridgeCommon.SafeString(() => def.GetGenderSpecificLabelCap(other));
                    if (!string.IsNullOrEmpty(label))
                        labels.Add(label);
                }
            }
            catch { }
            row["relations"] = labels;

            // ---- social memories. memories.Memories is `ldarg.0; ldfld; ret`.
            double memoryOffset = 0.0;
            var memoryRows = new List<object>();
            ThoughtHandler handler = null;
            try
            {
                var mood = pawn.needs != null ? pawn.needs.mood : null;
                handler = mood != null ? mood.thoughts : null;
            }
            catch { handler = null; }

            var social = new List<ISocialThought>();
            if (handler != null)
            {
                try
                {
                    var memories = handler.memories != null ? handler.memories.Memories : null;
                    if (memories != null)
                    {
                        for (var i = 0; i < memories.Count; i++)
                        {
                            var st = memories[i] as ISocialThought;
                            if (st == null)
                                continue;
                            if (BridgeCommon.Try<Pawn>(() => st.OtherPawn(), null) != other)
                                continue;
                            social.Add(st);
                        }
                    }
                }
                catch { }
            }

            // ---- situational social, from the cache, never recalculated
            var situational = new List<ISocialThought>();
            if (handler != null && SocialCacheField != null && SocialActiveField != null)
            {
                try
                {
                    var sit = handler.situational;
                    var dict = sit != null ? SocialCacheField.GetValue(sit) as IDictionary : null;
                    if (dict != null && dict.Contains(other))
                    {
                        situationalCached = true;
                        var entry = dict[other];
                        var list = entry != null ? SocialActiveField.GetValue(entry) as IEnumerable : null;
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                var st = item as ISocialThought;
                                if (st != null)
                                    situational.Add(st);
                            }
                        }
                    }
                }
                catch { situationalCached = false; }
            }

            // GetDistinctSocialThoughtGroups keeps the FIRST of each group and
            // drops the later duplicates; the same predicate, the same order.
            double situationalOffset = 0.0;
            var combined = new List<ISocialThought>();
            combined.AddRange(social);
            combined.AddRange(situational);
            var kept = new List<ISocialThought>();
            foreach (var candidate in combined)
            {
                var dup = false;
                foreach (var already in kept)
                {
                    var a = already as Thought;
                    var b = candidate as Thought;
                    if (a == null || b == null)
                        continue;
                    if (BridgeCommon.Try(() => a.GroupsWith(b), false))
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup)
                    continue;
                kept.Add(candidate);

                var offset = BridgeCommon.Try(() => candidate.OpinionOffset(), 0f);
                if (float.IsNaN(offset) || float.IsInfinity(offset))
                    offset = 0f;
                if (situational.Contains(candidate))
                    situationalOffset += offset;
                else
                    memoryOffset += offset;

                if (Math.Abs(offset) >= 0.5)
                {
                    var mr = new Dictionary<string, object>();
                    var asThought = candidate as Thought;
                    mr["label"] = asThought != null ? BridgeCommon.SafeString(() => asThought.LabelCap) : null;
                    mr["opinionOffset"] = Math.Round((double)offset, 1);
                    memoryRows.Add(mr);
                }
            }

            // ---- OpinionOf's own tail, reproduced
            var total = relationOffset + memoryOffset + situationalOffset;
            if (Math.Abs(total) > 0.0001)
            {
                var factor = 1.0;
                try
                {
                    var hediffs = pawn.health != null && pawn.health.hediffSet != null
                        ? pawn.health.hediffSet.hediffs
                        : null;
                    if (hediffs != null)
                    {
                        for (var i = 0; i < hediffs.Count; i++)
                        {
                            var stage = BridgeCommon.Try<HediffStage>(() => hediffs[i].CurStage, null);
                            if (stage != null)
                                factor *= stage.opinionOfOthersFactor;
                        }
                    }
                }
                catch { }
                // Mathf.RoundToInt is Math.Round's default (to even), so this is
                // the same rounding OpinionOf itself does.
                total = Math.Round(total * factor);
            }
            if (total > 0 && BridgeCommon.Try(() => pawn.HostileTo(other), false))
                total = 0;
            if (total > 100) total = 100;
            if (total < -100) total = -100;

            row["opinion"] = (int)total;
            row["opinionParts"] = new Dictionary<string, object>
            {
                { "relations", Math.Round(relationOffset, 1) },
                { "memories", Math.Round(memoryOffset, 1) },
                { "situational", Math.Round(situationalOffset, 1) }
            };
            row["situationalSocialCached"] = situationalCached;
            row["drivers"] = memoryRows;
            return row;
        }
    }
}
