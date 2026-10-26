using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameFacts.Common;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

internal static class NativeProtoSuppliesProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static void Set(object value, string name, object? fieldValue)
    {
        for (var type = value.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, Members) is { } field) { field.SetValue(value, fieldValue); return; }
        throw new MissingFieldException(value.GetType().FullName, name);
    }

    internal static int Invoke(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        var bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        var tools = bridge.GetType("RimGovernor.Native.NativeSuppliesObservationTools", true)!;
        var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
        var allow = bridge.GetType("RimGovernor.Native.NativeSupplyAllow", true)!;
        var identity = new Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
        var context = new ObservationContext { Identity = identity, Tick = 1, NativeGeneration = new ObservedGeneration.UnclaimedCase() };
        object[] tokenInputs = { identity, "Thing_Supply1", "Meal", 1, 2, 10, true, "" };
        string Token(object[] values) => (string)allow.GetMethod("Token", Flags)!.Invoke(null, values)!;
        var originalToken = Token(tokenInputs);
        Check(originalToken == Token(tokenInputs), "unchanged supply token stable across polling");
        object[] changedValues = { identity with { LoadToken = "new-load" }, "Thing_Supply2", "WoodLog", 2, 3, 9, false, "OtherFaction" };
        for (var i = 0; i < tokenInputs.Length; i++) {
            var changed = (object[])tokenInputs.Clone(); changed[i] = changedValues[i];
            Check(originalToken != Token(changed), "supply CAS binds field " + i);
        }
        foreach (var other in new[] { identity with { ColonyId = "other" }, identity with { MapId = 1 } }) {
            var changed = (object[])tokenInputs.Clone(); changed[0] = other;
            Check(originalToken != Token(changed), "supply CAS binds colony and map");
        }
        var filter = new StockFilter { Category = StockCategory.Haulable, Ownership = StockOwnership.Ours, IncludeHeld = true };
        var request = new ListSuppliesRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity }, Filter = filter };
        bool Valid(ListSuppliesRequest? value) => (bool)tools.GetMethod("Validate", Flags)!.Invoke(null, new object?[] { value, null })!;
        Check(Valid(request), "explicit census accepted");
        foreach (var category in Enum.GetValues<StockCategory>())
            Check(Valid(request with { Filter = filter with { Category = category, Ownership = StockOwnership.All, IncludeHeld = false } }), "supported category and explicit false accepted");
        Check(Valid(request with { Filter = filter with { DefNames = ["Modded_Resourceα"], Corpses = true, ForbiddenOnly = true, ExcludeChunks = true } }), "open identifiers and combined filters accepted");
        Check(!Valid(null) && !Valid(request with { Scope = null }) && !Valid(request with { Filter = null! }), "identity and filter required");
        foreach (var invalid in new[] {
            filter with { Category = (StockCategory)int.MaxValue }, filter with { Ownership = (StockOwnership)int.MaxValue },
            filter with { DefNames = ["a", "a"] }, filter with { DefNames = [""] }, filter with { DefNames = ["bad\0id"] },
            filter with { DefNames = [.. Enumerable.Range(0, 257).Select(i => "Def" + i)] },
            filter with { Region = new Rectangle { Minimum = new Cell(-1, 0), Maximum = new Cell(1, 1) } },
            filter with { Region = new Rectangle { Minimum = new Cell(2, 0), Maximum = new Cell(1, 1) } } })
            Check(!Valid(request with { Filter = invalid }), "invalid exact census filter refused");
        bool Ours(bool fogged, bool held, bool player, bool other, bool dead) => (bool)tools.GetMethod("IsOurs", Flags)!.Invoke(null, new object[] { fogged, held, player, other, dead })!;
        Check(Ours(false, false, false, false, false), "unclaimed loose stock is ours");
        Check(!Ours(true, false, true, false, false), "fogged stock excluded");
        Check(!Ours(false, false, false, true, false), "other faction loose stock excluded");
        Check(Ours(false, true, true, false, false), "live player holder stock is ours");
        Check(!Ours(false, true, false, true, false), "trader held stock excluded");
        Check(!Ours(false, true, true, false, true), "dead holder stock excluded");

        // These fixtures exercise projection, not running-game census traversal. Private game fixture fields
        // require reflection; all published records and output members below are compiler-checked.
        object New(string name) => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        bool PackedDefinition(string name, string nativeClass) {
            var def = New("Verse.ThingDef"); Set(def, "defName", name);
            Set(def, "thingClass", game.GetType(nativeClass, true)!);
            return (bool)tools.GetMethod("MatchesCategory", Flags)!.Invoke(null, new object[] { def, StockCategory.Packed, false })!;
        }
        foreach (var count in new[] { 25, 97 })
            for (var i = 0; i < count; i++)
                Check(PackedDefinition("InstalledPackedContainer" + i, "Verse.MinifiedThing"), "native packed class accepted outside container names");
        Check(!PackedDefinition("MinifiedThing", "Verse.Thing"), "a familiar container name cannot establish packed mechanics");
        object Thing(string definition, int count, int id) {
            var def = New("Verse.ThingDef"); Set(def, "defName", definition); Set(def, "label", definition);
            Set(def, "category", Enum.Parse(game.GetType("Verse.ThingCategory", true)!, "Item"));
            var thing = New("Verse.Thing"); Set(thing, "def", def); Set(thing, "thingIDNumber", id); Set(thing, "stackCount", count);
            Set(thing, "mapIndexOrState", (sbyte)-1); // Unspawned fixture: no live Allow snapshot lookup.
            return thing;
        }
        var entryType = tools.GetNestedType("StockEntry", BindingFlags.NonPublic)!;
        object Entry(object thing, object? holder = null, string? kind = null, bool ours = true) {
            var entry = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(entryType);
            Set(entry, "Thing", thing); Set(entry, "Holder", holder); Set(entry, "HolderKind", kind);
            Set(entry, "Position", Activator.CreateInstance(game.GetType("Verse.IntVec3", true)!, 2, 0, 2));
            Set(entry, "Ours", ours); Set(entry, "Forbidden", false); Set(entry, "PlayerFaction", ours); Set(entry, "OtherFaction", !ours);
            return entry;
        }
        ResourceStock Project(params object[] entries) {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
            foreach (var entry in entries) list.Add(entry);
            return (ResourceStock)tools.GetMethod("Project", Flags)!.Invoke(null, new object[] { list, context })!;
        }
        var modded = Project(Entry(Thing("Modded_ExoticOreXYZ", 4, 501)));
        Check(modded.DefName == "Modded_ExoticOreXYZ" && modded.Items.Count == 1 && modded.Items[0].Id.Contains("501"), "modded definition and item identity retained");
        Check(modded.Units == 4 && modded.Ours == 4, "modded stock aggregate exact");
        var corpse = Thing("Corpse_Human_Test", 1, 602);
        var corpseRow = Project(Entry(Thing("Modded_Ration", 2, 603), corpse, "corpse", false));
        Check(corpseRow.Holders.Count == 1 && corpseRow.Holders[0].HolderKind == HolderKind.Corpse && corpseRow.Holders[0].Units == 2, "corpse holder kind and units retained");
        var trader = Project(Entry(Thing("Modded_TradeGoods", 6, 702), Thing("Trader_Pawn_Test", 1, 701), "pawnInventory", false));
        Check(trader.Ours == 0 && trader.Holders[0].HolderKind == HolderKind.PawnInventory, "trader goods retain holder and zero ownership");
        var outer = Thing("Modded_Crate", 1, 801); var inner = Thing("Modded_Crate", 1, 802);
        var nested = Project(Entry(outer), Entry(inner, outer, "container"), Entry(Thing("Modded_Crate", 5, 803), inner, "container"));
        Check(nested.Stacks == 3 && nested.Holders.Count == 2 && nested.Holders[0].Holder != nested.Holders[1].Holder && nested.Holders.All(h => h.HolderKind == HolderKind.Container), "each projected nesting level retains its holder identity");
        var overflow = Project(Enumerable.Range(0, 300).Select(i => Entry(Thing("Modded_OverflowStack", 2, 900 + i))).ToArray());
        Check(overflow.Units == 600 && overflow.Stacks == 300 && overflow.OursUnforbidden == 600 && overflow.Items.Count == 300, "all 300 stacks retained without a list cap");
        var wide = Project(Entry(Thing("Modded_Wide", 1073741824, 1301), null, null, false), Entry(Thing("Modded_Wide", 1073741824, 1302), null, null, false));
        var snapshot = new SuppliesSnapshot { Context = context, Stocks = [wide] };
        var decoded = FactsJson.Deserialize<SuppliesSnapshot>(FactsJson.Serialize(snapshot));
        Check(decoded.Stocks[0].Units == 2147483648L && decoded.Stocks[0].Ours == 0 && decoded.Context == context, "snapshot preserves long totals, explicit zero and observation context");
        Console.WriteLine(checks + " compiled supplies boundary assertions passed; no gameplay assertions.");
        return 0;
    }
}
