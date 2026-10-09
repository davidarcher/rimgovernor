#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The guarded reads the supervised clock watches between ticks: the
    /// letter stack, live transient messages, active alerts and the spawned
    /// pawns. A native exception propagates to the watch's tool call (the
    /// host answers it as a named failure) except one alert's own
    /// getters; none of them recalculates an alert or calls a getter that logs
    /// (Verse.Log.Error pauses the game).
    /// </summary>
    internal static class GameWatchReads
    {
        private static readonly FieldInfo? ActiveAlertsField =
            BridgeCommon.PrivateInstanceField(typeof(AlertsReadout), "activeAlerts");

        private static readonly FieldInfo? LiveMessagesField =
            BridgeCommon.PrivateStaticField(typeof(Messages), "liveMessages");

        /// <summary>Both reflection watchers resolved on this build.</summary>
        internal static bool CoreWatchersAvailable => ActiveAlertsField != null && LiveMessagesField != null;

        internal static List<Letter> Letters()
        {
            var list = Find.LetterStack?.LettersListForReading;
            return list == null ? new List<Letter>() : list.Where(l => l != null).ToList();
        }

        internal static string SafeLetterId(Letter letter) => letter.GetUniqueLoadID();

        internal static List<Message> LiveMessages()
        {
            if (LiveMessagesField == null)
                return new List<Message>();
            var live = LiveMessagesField.GetValue(null) as List<Message>;
            return live == null ? new List<Message>() : live.Where(m => m != null).ToList();
        }

        internal static string SafeMessageId(Message message) => message.GetUniqueLoadID();

        internal static string? SafeMessageText(Message message) => message.text;
        internal static string? SafeMessageType(Message message) => message.def != null ? message.def.defName : null;
        internal static int SafeMessageTick(Message message) => message.startingTick;

        /// <summary>
        /// Active alerts at or above <paramref name="min"/>, as key/label pairs,
        /// from AlertsReadout.activeAlerts. The key is type|priority|label with
        /// the label's volatile " xN" count suffix stripped, so it is stable
        /// while the alert stays up; the value is the label as shown.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, string>> ActiveAlertKeys(
            AlertPriority min, List<KeyValuePair<string, string>>? sink)
        {
            var result = sink ?? new List<KeyValuePair<string, string>>();
            if (ActiveAlertsField == null)
                return result;
            var active = Find.Alerts == null ? null : ActiveAlertsField.GetValue(Find.Alerts) as List<Alert>;
            if (active == null)
                return result;
            foreach (var alert in active)
            {
                if (alert == null)
                    continue;
                AlertPriority priority;
                string label, type;
                try
                {
                    priority = alert.Priority;
                    if (priority < min)
                        continue;
                    label = alert.Label ?? string.Empty;
                    type = alert.GetType().FullName ?? alert.GetType().Name;
                }
                catch
                {
                    // A vanilla alert's Priority/Label getter throws for an alert whose own
                    // data is half-built; that one alert is skipped, the rest still read.
                    continue;
                }
                result.Add(new KeyValuePair<string, string>(type + "|" + priority + "|" + NormalizeAlertLabel(label), label));
            }
            return result;
        }

        // Strips a trailing " x<digits>" (BreakRiskAlertUtility.AlertLabel's
        // count suffix) when something is left in front of it.
        private static string NormalizeAlertLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
                return string.Empty;
            var s = label.TrimEnd();
            var end = s.Length;
            var i = end - 1;
            while (i >= 0 && s[i] >= '0' && s[i] <= '9')
                i--;
            if (i == end - 1 || i < 2 || (s[i] != 'x' && s[i] != 'X') || s[i - 1] != ' ')
                return s;
            return s.Substring(0, i - 1).TrimEnd();
        }

        internal static List<Pawn> SpawnedPawns(Map map)
        {
            var all = map?.mapPawns?.AllPawnsSpawned;
            return all == null ? new List<Pawn>() : all.ToList();
        }

        /// <summary>A manhunter, or a pawn of a faction hostile to the player.</summary>
        internal static bool IsHostile(Pawn pawn, out string reason)
        {
            reason = "none";
            var mental = pawn.MentalStateDef?.defName;
            if (!string.IsNullOrEmpty(mental) && mental!.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "manhunter:" + mental;
                return true;
            }
            var faction = pawn.Faction;
            var player = PlayerFaction();
            if (player != null && pawn.HostFaction == player)
            {
                // A held prisoner is hostile only while breaking out.
                if (!PrisonBreakUtility.IsPrisonBreaking(pawn)) return false;
                reason = "prison_break";
                return true;
            }
            if (faction == null || player == null || faction == player)
                return false;
            if (faction.HostileTo(player))
            {
                reason = "faction:" + faction.Name;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The prey of a PredatorHunt job, and whether the colony owns it (on
        /// the player faction, or held by it as a prisoner). job.targetA is the
        /// prey during the chase and its Corpse after the kill; both unwrap.
        /// </summary>
        internal static bool PreyBelongsToPlayer(Pawn predator, out Pawn? prey)
        {
            prey = null;
            var thing = predator?.CurJob?.targetA.Thing;
            prey = thing as Pawn ?? (thing as Corpse)?.InnerPawn;
            var player = PlayerFaction();
            if (prey == null || player == null)
                return false;
            return prey.Faction == player || prey.HostFaction == player;
        }

        internal static string? SafeName(Pawn pawn) => pawn.LabelShortCap.ToString();

        internal static bool SafeIsColonist(Pawn pawn) => pawn.IsColonist;
        internal static bool SafeDowned(Pawn pawn) => pawn.Downed;
        internal static bool SafeDead(Pawn pawn) => pawn.Dead;

        // Faction.OfPlayer logs (and so pauses) when the faction is missing.
        private static Faction? PlayerFaction() => Faction.OfPlayerSilentFail;
    }
}
