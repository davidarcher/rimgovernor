#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The read blocks that hang off a pawn's CONFIGURATION rather than their
    /// body or their mind: work priorities, the pawn-settings row (medical
    /// care, hostility response, self-tend, follow, allowed area, master) and
    /// thoughts. The typed pawn and work-settings reads emit them; there
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
    /// So nothing here asks for an opinion: the pawn read carries no relations.
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

        /// <summary>Active situational thoughts read from the cache without recalculating, plus the handler's own dirty flag; none for a pawn without a mood need. An unreadable cache field (a RimWorld rename) throws.</summary>
        internal static (List<Thought> Situational, bool Stale) LiveSituational(Pawn pawn)
        {
            var live = new List<Thought>();
            var sitHandler = pawn.needs?.mood?.thoughts?.situational;
            if (sitHandler == null) return (live, false);
            if (SituationalCacheField == null || SituationalDirtyField == null)
                throw new InvalidOperationException("SituationalThoughtHandler's cache fields are not readable.");
            var cached = SituationalCacheField.GetValue(sitHandler) as List<Thought_Situational>;
            if (cached != null) for (var i = 0; i < cached.Count; i++)
            {
                var s = cached[i];
                if (s != null && s.Active) live.Add(s);
            }
            return (live, (bool)SituationalDirtyField.GetValue(sitHandler)!);
        }

        /// <summary>Stack identical thoughts the way the Mood tab does. Returns (defName, count, moodOffsetTotal) rows, worst first.</summary>
        internal static List<(string? DefName, int Count, double Total)> GroupThoughtRows(List<Thought>? source)
        {
            var rows = new List<(string?, int, double)>();
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
                rows.Add((head.def?.defName, count, Math.Round(sum, 3)));
            }
            return rows.OrderBy(r => r.Item3).ToList();
        }

        private static float MoodOffsetOf(Thought t)
        {
            if (t == null) return 0f;
            if (t.CurStage == null) return 0f;
            var v = t.MoodOffset();
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }
    }
}
