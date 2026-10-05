#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
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
        /// <summary>Is the Work tab in numbered-priority mode? Null when there is
        /// no game or play settings yet, which is not the same as "simple mode"
        /// and is never collapsed into it.</summary>
        internal static bool? ManualPriorities()
        {
            if (Current.Game == null || Current.Game.playSettings == null)
                return null;
            return Current.Game.playSettings.useWorkPriorities;
        }

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
            var nested = typeof(SituationalThoughtHandler)
                .GetNestedType("CachedSocialThoughts", BindingFlags.NonPublic | BindingFlags.Public);
            return nested?.GetField("activeThoughts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        // ==================================================================
        // Typed-protocol accessors (N01.03 PawnSocial). Non-mutating reads that
        // return plain data so the Protocol/ layer can do its own protobuf
        // projection without this file depending on the generated Observations
        // types. A game read that throws propagates: the bridge host turns it
        // into a success:false reply (docs/developers/contracts/bridge-thrown-hop.md).
        // ==================================================================

        // The situational thought cache, read without recomputing it.
        private static readonly FieldInfo? SituationalCacheField =
            BridgeCommon.PrivateInstanceField(typeof(SituationalThoughtHandler), "cachedThoughts");
        private static readonly FieldInfo? SituationalDirtyField =
            BridgeCommon.PrivateInstanceField(typeof(SituationalThoughtHandler), "thoughtsDirty");

        /// <summary>Pure memories, never recalculated. Null only if the pawn has no ThoughtHandler.</summary>
        internal static List<Thought>? LiveMemories(Pawn pawn)
        {
            var handler = pawn.needs?.mood?.thoughts;
            if (handler == null) return null;
            var memHandler = handler.memories;
            if (memHandler == null) return new List<Thought>();
            var list = memHandler.Memories;
            return list != null ? list.Cast<Thought>().ToList() : new List<Thought>();
        }

        /// <summary>Active situational thoughts read from the cache without recalculating, plus the handler's own dirty flag. Null situational list means the cache field is unreadable (a RimWorld rename).</summary>
        internal static (List<Thought>? Situational, bool? Stale) LiveSituational(Pawn pawn)
        {
            var handler = pawn.needs?.mood?.thoughts;
            if (handler == null) return (null, null);
            var sitHandler = handler.situational;
            if (sitHandler == null || SituationalCacheField == null) return (null, null);
            var cached = SituationalCacheField.GetValue(sitHandler) as List<Thought_Situational>;
            var live = new List<Thought>();
            if (cached != null) for (var i = 0; i < cached.Count; i++)
            {
                var s = cached[i];
                if (s != null && s.Active) live.Add(s);
            }
            bool? stale = SituationalDirtyField != null ? (bool)SituationalDirtyField.GetValue(sitHandler) : (bool?)null;
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
                    if (!head.GroupsWith(other)) continue;
                    used[j] = true; count++; sum += MoodOffsetOf(other);
                }
                rows.Add((head.def?.defName, head.LabelCap, count, Math.Round(sum / count, 3), Math.Round(sum, 3)));
            }
            return rows.OrderBy(r => r.Item5).ToList();
        }

        private static float MoodOffsetOf(Thought t)
        {
            if (t == null) return 0f;
            if (t.CurStage == null) return 0f;
            var v = t.MoodOffset();
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }

        /// <summary>One row per other pawn with a formal direct relation, first relation def name if several.</summary>
        internal static Dictionary<Pawn, string> DirectRelationTargets(Pawn pawn)
        {
            var result = new Dictionary<Pawn, string>();
            var tracker = pawn.relations;
            if (tracker == null) return result;
            var list = tracker.DirectRelations;
            var raw = list != null ? list.ToList() : new List<DirectPawnRelation>();
            foreach (var r in raw)
            {
                if (r?.def == null || r.otherPawn == null || result.ContainsKey(r.otherPawn)) continue;
                result[r.otherPawn] = r.def.defName;
            }
            return result;
        }

        /// <summary>Reconstructed -100..100 opinion of `pawn` toward `other`. See the class remarks for why OpinionOf itself is never called.</summary>
        internal static int ReconstructedOpinion(Pawn pawn, Pawn other, out bool situationalCached)
        {
            situationalCached = false;
            double relationOffset = 0.0;
            foreach (var def in pawn.GetRelations(other)) if (def != null) relationOffset += def.opinionOffset;

            var handler = pawn.needs?.mood?.thoughts;
            var social = new List<ISocialThought>();
            if (handler != null)
            {
                var memories = handler.memories?.Memories;
                if (memories != null) for (var i = 0; i < memories.Count; i++)
                {
                    var st = memories[i] as ISocialThought;
                    if (st != null && st.OtherPawn() == other) social.Add(st);
                }
            }

            var situational = new List<ISocialThought>();
            if (handler != null && SocialCacheField != null && SocialActiveField != null)
            {
                var dict = handler.situational != null ? SocialCacheField.GetValue(handler.situational) as IDictionary : null;
                if (dict != null && dict.Contains(other))
                {
                    situationalCached = true;
                    var entry = dict[other];
                    var list = entry != null ? SocialActiveField.GetValue(entry) as IEnumerable : null;
                    if (list != null) foreach (var item in list) if (item is ISocialThought st) situational.Add(st);
                }
            }

            double memoryOffset = 0.0, situationalOffset = 0.0;
            var combined = new List<ISocialThought>(); combined.AddRange(social); combined.AddRange(situational);
            var kept = new List<ISocialThought>();
            foreach (var candidate in combined)
            {
                var dup = kept.Any(already => already is Thought a && candidate is Thought b && a.GroupsWith(b));
                if (dup) continue;
                kept.Add(candidate);
                var offset = candidate.OpinionOffset();
                if (float.IsNaN(offset) || float.IsInfinity(offset)) offset = 0f;
                if (situational.Contains(candidate)) situationalOffset += offset; else memoryOffset += offset;
            }

            var total = relationOffset + memoryOffset + situationalOffset;
            if (Math.Abs(total) > 0.0001)
            {
                var factor = 1.0;
                var hediffs = pawn.health?.hediffSet?.hediffs;
                if (hediffs != null) for (var i = 0; i < hediffs.Count; i++)
                {
                    var stage = hediffs[i].CurStage;
                    if (stage != null) factor *= stage.opinionOfOthersFactor;
                }
                total = Math.Round(total * factor);
            }
            if (total > 0 && pawn.HostileTo(other)) total = 0;
            return (int)Math.Max(-100, Math.Min(100, total));
        }
    }
}
