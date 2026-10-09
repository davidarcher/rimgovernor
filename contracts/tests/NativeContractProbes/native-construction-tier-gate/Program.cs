using System;
using System.Collections.Generic;
using HomeBridge.BridgeTools;

internal static class NativeConstructionTierGateProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static KeyValuePair<int, Func<bool>> Site(int tier, bool deliverable = true) =>
        new KeyValuePair<int, Func<bool>>(tier, () => deliverable);
    internal static void Invoke()
    {
        const int U = ConstructionTierGatePolicy.Untiered;
        Check(!ConstructionTierGatePolicy.Allows(5, new[] { Site(0) }), "lower tier needing the material blocks");
        Check(!ConstructionTierGatePolicy.Allows(1, new[] { Site(5), Site(0) }), "any lower site blocks");
        Check(ConstructionTierGatePolicy.Allows(0, new[] { Site(5) }), "higher tier does not block");
        Check(ConstructionTierGatePolicy.Allows(3, new[] { Site(3) }), "equal tiers are not ordered");
        Check(ConstructionTierGatePolicy.Allows(5, new KeyValuePair<int, Func<bool>>[0]), "nobody needs the material");
        Check(ConstructionTierGatePolicy.Allows(U, new[] { Site(0) }), "untiered site is never gated");
        Check(ConstructionTierGatePolicy.Allows(5, new[] { Site(U) }), "untiered site never gates");
        Check(ConstructionTierGatePolicy.Allows(5, new[] { Site(0, false) }), "undeliverable lower site does not block");
        Check(!ConstructionTierGatePolicy.Allows(5, new[] { Site(0, false), Site(2) }), "a deliverable lower site still blocks");
        var asked = 0;
        Check(!ConstructionTierGatePolicy.Allows(5, new[] { new KeyValuePair<int, Func<bool>>(0, () => { asked++; return true; }) }) && asked == 1,
            "deliverability is checked lazily");
        asked = 0;
        Check(ConstructionTierGatePolicy.Allows(1, new[] { new KeyValuePair<int, Func<bool>>(4, () => { asked++; return true; }) }) && asked == 0,
            "higher sites are never probed");
        Console.WriteLine("Construction tier gate passed " + checks + " assertions; pure policy, no native gameplay.");
    }
}
