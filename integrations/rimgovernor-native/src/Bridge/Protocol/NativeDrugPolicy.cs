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
        internal static bool Social(DrugPolicy p)
        {
            for (var i = 0; i < p.Count; i++) {
                var e = p[i];
                if (e.allowedForJoy != (e.drug.defName == "Beer" || e.drug.defName == "SmokeleafJoint")
                    || e.allowedForAddiction || e.allowScheduled || e.takeToInventory != 0) return false;
            }
            return true;
        }
        internal static bool Matches(Pawn pawn, string name) => pawn.drugs?.CurrentPolicy is DrugPolicy p && p.label == name && Social(p) && Current.Game.drugPolicyDatabase.DefaultDrugPolicy() == p;
        internal static string Name(Pawn pawn) => pawn.drugs?.CurrentPolicy is DrugPolicy p && Social(p) && Current.Game.drugPolicyDatabase.DefaultDrugPolicy() == p ? p.label : "";
        // Hash only this pawn's settings (#685): Apply moves the colony default, so a
        // default-policy signature would move every sibling's token on the first write.
        internal static string Token(string work, Pawn pawn)
        {
            using (var hash = SHA256.Create()) return "drug-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(work + Signature(pawn.drugs?.CurrentPolicy) + "/" + Name(pawn) + "/" + Writable(pawn)))).Replace("-", "");
        }
        internal static void Apply(Pawn pawn, string name)
        {
            var db = Current.Game.drugPolicyDatabase;

            var policy = db.AllPolicies.FirstOrDefault(p => p.label == name) ?? db.MakeNewDrugPolicy();
            policy.label = name;
            for (var i = 0; i < policy.Count; i++) {
                var e = policy[i];
                e.allowedForJoy = e.drug.defName == "Beer" || e.drug.defName == "SmokeleafJoint";
                e.allowedForAddiction = false; e.allowScheduled = false; e.takeToInventory = 0;
            }

            pawn.drugs.CurrentPolicy = policy;
            db.SetDefault(policy);
        }
    }
}
