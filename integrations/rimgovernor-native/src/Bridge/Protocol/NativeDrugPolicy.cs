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
        internal static string Name(Pawn pawn) => pawn.drugs?.CurrentPolicy?.label ?? "";
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
