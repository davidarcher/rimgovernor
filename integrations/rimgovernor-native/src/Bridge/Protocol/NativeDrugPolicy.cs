#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class NativeDrugPolicy
    {
        internal static void Install() => SocialBeerBill.Install();
        internal static string Signature(DrugPolicy? policy)
        {
            if (policy == null) return "none";
            var text = policy.GetUniqueLoadID() + "/" + policy.label;
            for (var i = 0; i < policy.Count; i++) {
                var e = policy[i];
                text += FormattableString.Invariant($"/{e.drug.defName}:{e.allowedForAddiction}:{e.allowedForJoy}:{e.allowScheduled}:{e.daysFrequency}:{e.onlyIfMoodBelow}:{e.onlyIfJoyBelow}:{e.takeToInventory}");
            }
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }
        internal static bool Writable(Pawn pawn) => pawn.drugs?.CurrentPolicy != null;
        // A pawn the player moved off the colony default keeps that choice; only default-policy pawns are
        // assigned, and the colony default itself (a player setting) is never changed.
        internal static bool OnDefault(Pawn pawn) => pawn.drugs?.CurrentPolicy is DrugPolicy p && Current.Game.drugPolicyDatabase.DefaultDrugPolicy() == p;
        internal static DrugPolicy? Named(string name) => Current.Game.drugPolicyDatabase.AllPolicies.FirstOrDefault(p => p.label == name);
        // An existing same-named policy is never rewritten: a customized one blocks assignment.
        internal static bool Conflicts(string name) => Named(name) is DrugPolicy p && !Social(p);
        internal static bool Social(DrugPolicy p)
        {
            for (var i = 0; i < p.Count; i++) {
                var e = p[i];
                if (e.allowedForJoy != (e.drug.defName == "Beer" || e.drug.defName == "SmokeleafJoint")
                    || e.allowedForAddiction || e.allowScheduled || e.takeToInventory != 0) return false;
            }
            return true;
        }
        internal static bool Matches(Pawn pawn, string name) => pawn.drugs?.CurrentPolicy is DrugPolicy p && p.label == name && Social(p);
        internal static string Name(Pawn pawn) => pawn.drugs?.CurrentPolicy is DrugPolicy p ? p.label : "";
        internal static string Token(string work, Pawn pawn)
        {
            using (var hash = SHA256.Create()) return "drug-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(work + Signature(pawn.drugs?.CurrentPolicy) + Signature(Current.Game.drugPolicyDatabase.DefaultDrugPolicy()) + Writable(pawn) + OnDefault(pawn)))).Replace("-", "");
        }
        internal static void Apply(Pawn pawn, string name)
        {
            var db = Current.Game.drugPolicyDatabase;

            var policy = Named(name);
            if (policy == null) {
                policy = db.MakeNewDrugPolicy();
                policy.label = name;
                for (var i = 0; i < policy.Count; i++) {
                    var e = policy[i];
                    e.allowedForJoy = e.drug.defName == "Beer" || e.drug.defName == "SmokeleafJoint";
                    e.allowedForAddiction = false; e.allowScheduled = false; e.takeToInventory = 0;
                }
            }

            pawn.drugs.CurrentPolicy = policy;
        }
    }
}
