#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
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
    /// <summary>
    /// The read blocks that hang off a pawn's CONFIGURATION rather than their
    /// body or their mind: work priorities, the pawn-settings row (medical
    /// care, hostility response, self-tend, follow, allowed area, master) and
    /// relationships. The typed pawn and work-settings reads emit them; there
    /// is one reader, so no two surfaces can disagree.
    ///
    /// ## Hazards, IL-scanned against Assembly-CSharp 1.6.9676.17735
    ///
    /// ### `Pawn_WorkSettings.GetPriority` is NOT a pure read
    ///
    /// Its first line is `ConfirmInitializedDebug()`, whose whole body is:
    ///
    ///     if (priorities == null) { Log.Error(pawn + " did not have work
    ///                                         settings initialized.");
    ///                               EnableAndInitialize(); }
    ///
    /// `Verse.Log.Error` calls `TickManager.Pause()`, and `EnableAndInitialize`
    /// WRITES a fresh priority table onto the pawn. So asking an uninitialised
    /// pawn what its work priorities are would pause the colony AND assign it
    /// six jobs. `EverWork` / `Initialized` are both `priorities != null` and
    /// neither calls the guard, so they are tested FIRST here and `GetPriority`
    /// is never reached otherwise. `WorkIsActive` and `SetPriority` open with the
    /// same guard and are gated the same way.
    ///
    /// ### `GetPriority` LIES in simple mode, on purpose
    ///
    /// Its tail is `if (Humanlike && num > 0 && !Find.PlaySettings.useWorkPriorities)
    /// return 3;` — with the Work tab in checkbox mode, every active job reports
    /// priority 3 whatever is stored. Both numbers are emitted: `priority` is
    /// what the game acts on, `priorityStored` is the raw cell out of the private
    /// `DefMap&lt;WorkTypeDef,int&gt; priorities`, read through the map's own
    /// public indexer (`values[def.index]`, no guard, no masking). They differ
    /// only in simple mode, and `manualPriorities` says which mode is on.
    ///
    /// ### `Pawn_RelationsTracker.OpinionOf` can DELETE and CREATE memories
    ///
    /// The same trap as `ThoughtHandler.GetDistinctMoodThoughtGroups`, one field
    /// over, and it is not marked anywhere in the game:
    ///
    ///     OpinionOf -> ThoughtHandler.TotalOpinionOffset
    ///       -> GetDistinctSocialThoughtGroups -> GetSocialThoughts
    ///       -> SituationalThoughtHandler.AppendSocialThoughts
    ///       -> CheckRecalculateSocialThoughts   (creates the per-pawn cache
    ///                                            entry if absent)
    ///       -> Thought_SituationalSocial.RecalculateState
    ///       -> Thought_Situational.Notify_BecameActive   -> RemoveMemoriesOfDef
    ///          Thought_Situational.Notify_BecameInactive -> TryGainMemory
    ///
    /// A cache entry that does not exist yet is created with
    /// `lastRecalculationTick = -99999`, so `ShouldRecalculateState` is true on
    /// the FIRST query and every social situational thought transitions
    /// inactive -> active — which is exactly the branch that removes memories.
    /// Asking "what does Ada think of Bo?" would edit both colonists' minds.
    ///
    /// So `opinion` is RECONSTRUCTED from `OpinionOf`'s own arithmetic over pure
    /// reads, and says so in `opinionMethod`:
    ///
    ///   * relation offsets: `pawn.GetRelations(other)` summing
    ///     `PawnRelationDef.opinionOffset` — the same enumeration the social card
    ///     labels rows with. Its workers only read.
    ///   * memory offsets: `memories.Memories` filtered to `ISocialThought` whose
    ///     `OtherPawn()` is the other pawn, grouped with `Thought.GroupsWith`
    ///     (RimWorld's own predicate) and summed with
    ///     `Thought_MemorySocial.OpinionOffset()`, which is a pure read.
    ///   * situational-social offsets: the handler's private
    ///     `cachedSocialThoughts` dictionary, read by reflection WITHOUT
    ///     recalculating. When it holds no entry for the other pawn — normal, it
    ///     is only filled when something asked — `situationalSocialCached` is
    ///     false and the number is a floor, said out loud rather than passed off
    ///     as a total.
    ///   * then `OpinionOf`'s own tail: multiply by every hediff stage's
    ///     `opinionOfOthersFactor`, zero a positive opinion toward someone this
    ///     pawn is hostile to, clamp to -100..100.
    ///
    /// ### Everything else on these blocks stores nothing
    ///
    /// `Pawn_TimetableTracker.times` / `.GetAssignment(h)` / `.CurrentAssignment`
    /// (indexes the list), every `Pawn_PlayerSettings` field (`medCare`,
    /// `hostilityResponse`, `selfTend`, `followDrafted`, `followFieldwork` are
    /// public fields; `Master`'s getter is `return master;`),
    /// `AreaRestrictionInPawnCurrentMap`'s getter (a dictionary lookup),
    /// `AreaManager.AllAreas`, `Pawn.WorkTypeIsDisabled` (which memoises
    /// `cachedDisabledWorkTypes` — a memo behind a Scribe-mode reset, the same
    /// class as `PainTotal`) and `Pawn_TrainingTracker.HasLearned` were all
    /// IL-scanned and write no game state.
    ///
    /// **`EffectiveAreaRestrictionInPawnCurrentMap` is deliberately not read.**
    /// Its `RespectsAllowedArea` branch calls `Faction.OfPlayer`, whose body is
    /// `OfPlayerSilentFail` followed by `Log.Error` — the pause again. The plain
    /// `AreaRestrictionInPawnCurrentMap` is what the Assign tab shows and needs
    /// no faction lookup.
    /// </summary>
    internal static class PawnSettingsRead
    {
        // ==================================================================
        // work{}
        // ==================================================================

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

        // ==================================================================
        // settings{}
        // ==================================================================

        /// <summary>
        /// The Assign tab's row for one pawn: medical care, hostility response,
        /// self-tend, the two follow toggles, the allowed area and the master.
        /// **Never null.**
        /// </summary>
        internal static Dictionary<string, object?> SettingsBlock(Pawn pawn)
        {
            var block = new Dictionary<string, object?>();

            block["thingId"] = BridgeCommon.SafeString(() => pawn.ThingID);

            Pawn_PlayerSettings? ps = null;
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
            var area = BridgeCommon.Try<Area?>(() => ps.AreaRestrictionInPawnCurrentMap, null);
            block["allowedArea"] = area != null ? BridgeCommon.SafeString(() => area.Label) : null;
            block["allowedAreaIsUnrestricted"] = area == null;
            block["allowedAreaCellCount"] = area != null ? BridgeCommon.TryN(() => area.TrueCount) : null;
            block["supportsAllowedAreas"] = BridgeCommon.TryN(() => ps.SupportsAllowedAreas);

            var master = BridgeCommon.Try<Pawn?>(() => ps.Master, null);
            block["master"] = master != null ? BridgeCommon.SafeString(() => master.LabelShortCap.ToString()) : null;
            block["masterThingId"] = master != null ? BridgeCommon.SafeString(() => master.ThingID) : null;
            block["note"] = "allowedArea null means UNRESTRICTED — the whole map — which is a real setting, not a missing read; allowedAreaIsUnrestricted says so as a bool. selfTend is OFF by default for every colonist including new joiners.";
            return block;
        }

        // ==================================================================
        // relations{}
        // ==================================================================

        /// <summary>The private per-other-pawn social-thought cache. Read
        /// WITHOUT calling AppendSocialThoughts, which recalculates and can
        /// delete memories. See the class remarks.</summary>
        private static readonly FieldInfo? SocialCacheField =
            BridgeCommon.PrivateInstanceField(typeof(SituationalThoughtHandler), "cachedSocialThoughts");

        /// <summary>The `activeThoughts` list inside a cache entry. The entry
        /// type is a private nested class; the list it holds is
        /// `List&lt;Thought_SituationalSocial&gt;`, which is public.</summary>
        private static readonly FieldInfo? SocialActiveField = FindSocialActiveField();

        private static FieldInfo? FindSocialActiveField()
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
        internal static Dictionary<string, object?> RelationsBlock(Pawn pawn, List<Pawn> colonists)
        {
            var block = new Dictionary<string, object?>();

            Pawn_RelationsTracker? tracker = null;
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
                    var row = new Dictionary<string, object?>();
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
                    row["otherDead"] = other == null ? (object?)null : BridgeCommon.Try(() => other.Dead, false);
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
                        .Cast<Dictionary<string, object?>>()
                        .OrderBy(r =>
                        {
                            object? v;
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

        private static Dictionary<string, object?> OpinionRow(Pawn pawn, Pawn other, out bool situationalCached)
        {
            situationalCached = false;

            var row = new Dictionary<string, object?>();
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
                    if (label != null && label.Length > 0)
                        labels.Add(label);
                }
            }
            catch { }
            row["relations"] = labels;

            // ---- social memories. memories.Memories is `ldarg.0; ldfld; ret`.
            double memoryOffset = 0.0;
            var memoryRows = new List<object>();
            ThoughtHandler? handler = null;
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
                            if (BridgeCommon.Try<Pawn?>(() => st.OtherPawn(), null) != other)
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
                    var mr = new Dictionary<string, object?>();
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
                            var stage = BridgeCommon.Try<HediffStage?>(() => hediffs[i].CurStage, null);
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
            row["opinionParts"] = new Dictionary<string, object?>
            {
                { "relations", Math.Round(relationOffset, 1) },
                { "memories", Math.Round(memoryOffset, 1) },
                { "situational", Math.Round(situationalOffset, 1) }
            };
            row["situationalSocialCached"] = situationalCached;
            row["drivers"] = memoryRows;
            return row;
        }

        // ==================================================================
        // Typed-protocol accessors (N01.03 PawnSocial). Same non-mutating reads
        // as ThoughtsBlock/RelationsBlock/OpinionRow above; these return plain
        // data so the Protocol/ layer can do its own protobuf projection without
        // this file depending on the generated Observations types.
        // ==================================================================

        // The situational thought cache, read without recomputing it.
        private static readonly FieldInfo? SituationalCacheField =
            BridgeCommon.PrivateInstanceField(typeof(SituationalThoughtHandler), "cachedThoughts");
        private static readonly FieldInfo? SituationalDirtyField =
            BridgeCommon.PrivateInstanceField(typeof(SituationalThoughtHandler), "thoughtsDirty");

        /// <summary>Pure memories, never recalculated. Null only if the pawn has no ThoughtHandler.</summary>
        internal static List<Thought>? LiveMemories(Pawn pawn)
        {
            Need_Mood? mood; try { mood = pawn.needs?.mood; } catch { mood = null; }
            ThoughtHandler? handler; try { handler = mood?.thoughts; } catch { handler = null; }
            if (handler == null) return null;
            MemoryThoughtHandler? memHandler; try { memHandler = handler.memories; } catch { memHandler = null; }
            if (memHandler == null) return new List<Thought>();
            try { var list = memHandler.Memories; return list != null ? list.Cast<Thought>().ToList() : new List<Thought>(); }
            catch { return new List<Thought>(); }
        }

        /// <summary>Active situational thoughts read from the cache without recalculating, plus the handler's own dirty flag. Null situational list means the cache field is unreadable (a RimWorld rename).</summary>
        internal static (List<Thought>? Situational, bool? Stale) LiveSituational(Pawn pawn)
        {
            Need_Mood? mood; try { mood = pawn.needs?.mood; } catch { mood = null; }
            ThoughtHandler? handler; try { handler = mood?.thoughts; } catch { handler = null; }
            if (handler == null) return (null, null);
            SituationalThoughtHandler? sitHandler; try { sitHandler = handler.situational; } catch { sitHandler = null; }
            if (sitHandler == null || SituationalCacheField == null) return (null, null);
            List<Thought_Situational>? cached;
            try { cached = SituationalCacheField.GetValue(sitHandler) as List<Thought_Situational>; } catch { cached = null; }
            var live = new List<Thought>();
            if (cached != null) for (var i = 0; i < cached.Count; i++)
            {
                var s = cached[i];
                if (s != null && BridgeCommon.Try(() => s.Active, false)) live.Add(s);
            }
            bool? stale = SituationalDirtyField != null ? BridgeCommon.Try<bool?>(() => (bool)SituationalDirtyField.GetValue(sitHandler), null) : (bool?)null;
            return (live, stale);
        }

        /// <summary>Stack identical thoughts the way the Mood tab does. Returns (defName, label, count, moodOffsetEach, moodOffsetTotal) rows, worst first.</summary>
        internal static List<(string? DefName, string? Label, int Count, double Each, double Total)> GroupThoughtRows(List<Thought> source)
        {
            var rows = new List<(string?, string?, int, double, double)>();
            if (source == null || source.Count == 0) return rows;
            var used = new bool[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var head = source[i];
                if (head == null) continue;
                var count = 1; var sum = (double)MoodOffsetOf(head);
                for (var j = i + 1; j < source.Count; j++)
                {
                    if (used[j]) continue;
                    var other = source[j];
                    if (other == null) continue;
                    if (!BridgeCommon.Try(() => head.GroupsWith(other), false)) continue;
                    used[j] = true; count++; sum += MoodOffsetOf(other);
                }
                rows.Add((BridgeCommon.Try<string?>(() => head.def?.defName, null), BridgeCommon.Try<string?>(() => head.LabelCap, null), count, Math.Round(sum / count, 3), Math.Round(sum, 3)));
            }
            return rows.OrderBy(r => r.Item5).ToList();
        }

        private static float MoodOffsetOf(Thought t)
        {
            if (t == null) return 0f;
            var stage = BridgeCommon.Try<ThoughtStage?>(() => t.CurStage, null);
            if (stage == null) return 0f;
            var v = BridgeCommon.Try(() => t.MoodOffset(), 0f);
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }

        /// <summary>One row per other pawn with a formal direct relation, first relation def name if several.</summary>
        internal static Dictionary<Pawn, string> DirectRelationTargets(Pawn pawn)
        {
            var result = new Dictionary<Pawn, string>();
            Pawn_RelationsTracker? tracker; try { tracker = pawn.relations; } catch { tracker = null; }
            if (tracker == null) return result;
            List<DirectPawnRelation> raw;
            try { var list = tracker.DirectRelations; raw = list != null ? list.ToList() : new List<DirectPawnRelation>(); }
            catch { raw = new List<DirectPawnRelation>(); }
            foreach (var r in raw)
            {
                if (r?.def == null || r.otherPawn == null || result.ContainsKey(r.otherPawn)) continue;
                result[r.otherPawn] = r.def.defName;
            }
            return result;
        }

        /// <summary>Reconstructed -100..100 opinion of `pawn` toward `other`, ported from OpinionRow without the UI-only labels/drivers detail. See the class remarks for why OpinionOf itself is never called.</summary>
        internal static int ReconstructedOpinion(Pawn pawn, Pawn other, out bool situationalCached)
        {
            situationalCached = false;
            double relationOffset = 0.0;
            try { foreach (var def in pawn.GetRelations(other)) if (def != null) relationOffset += BridgeCommon.Try(() => def.opinionOffset, 0); } catch { }

            ThoughtHandler? handler; try { handler = pawn.needs?.mood?.thoughts; } catch { handler = null; }
            var social = new List<ISocialThought>();
            if (handler != null) try
            {
                var memories = handler.memories?.Memories;
                if (memories != null) for (var i = 0; i < memories.Count; i++)
                {
                    var st = memories[i] as ISocialThought;
                    if (st != null && BridgeCommon.Try<Pawn?>(() => st.OtherPawn(), null) == other) social.Add(st);
                }
            } catch { }

            var situational = new List<ISocialThought>();
            if (handler != null && SocialCacheField != null && SocialActiveField != null) try
            {
                var dict = handler.situational != null ? SocialCacheField.GetValue(handler.situational) as IDictionary : null;
                if (dict != null && dict.Contains(other))
                {
                    situationalCached = true;
                    var entry = dict[other];
                    var list = entry != null ? SocialActiveField.GetValue(entry) as IEnumerable : null;
                    if (list != null) foreach (var item in list) if (item is ISocialThought st) situational.Add(st);
                }
            } catch { situationalCached = false; }

            double memoryOffset = 0.0, situationalOffset = 0.0;
            var combined = new List<ISocialThought>(); combined.AddRange(social); combined.AddRange(situational);
            var kept = new List<ISocialThought>();
            foreach (var candidate in combined)
            {
                var dup = kept.Any(already => already is Thought a && candidate is Thought b && BridgeCommon.Try(() => a.GroupsWith(b), false));
                if (dup) continue;
                kept.Add(candidate);
                var offset = BridgeCommon.Try(() => candidate.OpinionOffset(), 0f);
                if (float.IsNaN(offset) || float.IsInfinity(offset)) offset = 0f;
                if (situational.Contains(candidate)) situationalOffset += offset; else memoryOffset += offset;
            }

            var total = relationOffset + memoryOffset + situationalOffset;
            if (Math.Abs(total) > 0.0001)
            {
                var factor = 1.0;
                try
                {
                    var hediffs = pawn.health?.hediffSet?.hediffs;
                    if (hediffs != null) for (var i = 0; i < hediffs.Count; i++)
                    {
                        var stage = BridgeCommon.Try<HediffStage?>(() => hediffs[i].CurStage, null);
                        if (stage != null) factor *= stage.opinionOfOthersFactor;
                    }
                } catch { }
                total = Math.Round(total * factor);
            }
            if (total > 0 && BridgeCommon.Try(() => pawn.HostileTo(other), false)) total = 0;
            return (int)Math.Max(-100, Math.Min(100, total));
        }
    }
}
