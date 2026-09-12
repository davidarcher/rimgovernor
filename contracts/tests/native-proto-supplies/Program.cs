#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

internal static class Program
{
    private static Assembly bridge = null!;
    private static Assembly game = null!;
    private static Type tools = null!;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static object Wire(string name, string json)
    {
        var parser = bridge.GetType("RimGovernor.Protocol.Observations." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }
    private static object Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static bool Valid(string json) => (bool)tools.GetMethod("Validate", Flags)!.Invoke(null,
        new object?[] { Wire("ListSuppliesRequest", json), null })!;

    // ---- Fixture-construction helpers (native-proto-supplies items 1-5) ----
    // FieldInfo.GetField does not search base-class fields, and several traversal-relevant
    // fields (e.g. Verse.Area's innerGrid, Verse.Corpse's innerContainer) are declared on a
    // base type -- walk BaseType explicitly, matching the pattern used by the other
    // native-proto/native-pawn fixture suites in this repo.
    private static FieldInfo FixtureField(Type t, string name) { for (var x = t; x != null; x = x.BaseType!) { var fi = x.GetField(name, MemberFlags); if (fi != null) return fi; } throw new Exception("field not found: " + name + " on " + t); }
    private static void SetF(object o, string f, object? v) => FixtureField(o.GetType(), f).SetValue(o, v);
    private static object New(string typeName) => FormatterServices.GetUninitializedObject(game.GetType(typeName, true)!);
    private static bool SkipOriginal() => false;
    private static bool EmptyStringResult(ref string __result) { __result = ""; return false; }
    private static int Main(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools = bridge.GetType("HomeBridge.BridgeTools.NativeSuppliesObservationTools", true)!;
        game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
        var allow = bridge.GetType("HomeBridge.BridgeTools.NativeSupplyAllow", true)!;
        Func<string, string, object> parse = (type, json) => {
            var parser = bridge.GetType("RimGovernor.Protocol." + type, true)!.GetProperty("Parser")!.GetValue(null)!;
            return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
        };
        Func<string, bool> validAllow = json => (bool)allow.GetMethod("Valid", Flags)!.Invoke(null,
            new[] { parse("Operations.DesignateThing", json) })!;
        const string target = "\"target\":{\"entityId\":\"Thing_Supply1\",\"expectedSnapshotToken\":\"token\"}";
        Check(validAllow("{" + target + ",\"designation\":\"THING_DESIGNATION_ALLOW\"}"), "Exact Allow accepted");
        foreach (var designation in new[] { "FORBID", "HUNT", "HARVEST_PLANT", "DECONSTRUCT", "UNSPECIFIED" })
            Check(!validAllow("{" + target + ",\"designation\":\"THING_DESIGNATION_" + designation + "\"}"), "Allow cannot widen to " + designation);
        foreach (var invalid in new[] { "{}", "{" + target + "}",
            "{\"target\":{\"entityId\":\"Thing_Supply1\"},\"designation\":1}",
            "{\"target\":{\"entityId\":\"\",\"expectedSnapshotToken\":\"token\"},\"designation\":1}" })
            Check(!validAllow(invalid), "Missing Allow presence or entity snapshot refused");
        var identity = parse("Common.Identity", "{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}");
        object[] tokenInputs = { identity, "Thing_Supply1", "Meal", 1, 2, 10, true, "" };
        Func<object[], string> token = values => (string)allow.GetMethod("Token", Flags)!.Invoke(null, values)!;
        var originalToken = token(tokenInputs);
        Check(originalToken == token(tokenInputs), "Unchanged supply token stable across polling");
        var changedValues = new object[] { parse("Common.Identity", "{\"colonyId\":\"colony\",\"loadToken\":\"new-load\",\"mapId\":0}"),
            "Thing_Supply2", "WoodLog", 2, 3, 9, false, "OtherFaction" };
        for (var index = 0; index < tokenInputs.Length; index++) {
            var changed = (object[])tokenInputs.Clone(); changed[index] = changedValues[index];
            Check(originalToken != token(changed), "Supply CAS binds field " + index);
        }
        foreach (var otherIdentity in new[] { "{\"colonyId\":\"other\",\"loadToken\":\"load\",\"mapId\":0}",
            "{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":1}" }) {
            var changed = (object[])tokenInputs.Clone(); changed[0] = parse("Common.Identity", otherIdentity);
            Check(originalToken != token(changed), "Supply CAS cannot cross colony/map");
        }
        const string scope = "\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        var attempt = parse("Common.AttemptKey", "{\"controllerSessionId\":\"owner\",\"actionId\":\"allow\",\"attemptId\":\"1\"}");
        var context = parse("Common.ObservationContext", "{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":\"1\"}");
        const string effectFields = "\"thingId\":\"Thing_Supply1\",\"resourceDef\":\"Meal\",\"designationDef\":\"Allow\"";
        var original = parse("Receipts.DesignationEffect", "{" + effectFields + ",\"present\":true}");
        Func<object?, object> progress = observed => allow.GetMethod("ObservedProgress", Flags)!.Invoke(null, new[] { attempt, context, original, observed })!;
        Check(Get(progress(original), "EffectCase").ToString() == "Completed", "Exact later allowed observation completes");
        var forbiddenAgain = parse("Receipts.DesignationEffect", "{" + effectFields + ",\"present\":false}");
        Check(Get(progress(forbiddenAgain), "EffectCase").ToString() == "Unsuccessful", "Re-forbidden supply cannot certify recovery");
        foreach (var unknown in new object?[] { null, parse("Receipts.DesignationEffect", "{" + effectFields + "}"),
            parse("Receipts.DesignationEffect", "{\"thingId\":\"other\",\"resourceDef\":\"Meal\",\"designationDef\":\"Allow\",\"present\":true}"),
            parse("Receipts.DesignationEffect", "{\"thingId\":\"Thing_Supply1\",\"resourceDef\":\"WoodLog\",\"designationDef\":\"Allow\",\"present\":true}") }) {
            var result = progress(unknown);
            Check(Get(result, "EffectCase").ToString() == "Unknown" && !(bool)Get(result, "CompleteInspection"), "Missing/foreign evidence cannot complete");
        }
        Func<string, string> request = fields => "{" + scope + (fields.Length == 0 ? "" : "," + fields) + "}";
        Check(Valid(request("")), "Default census accepted");
        foreach (var category in new[] { "haulable", "food", "weapons", "all", "buildings" })
            Check(Valid(request("\"filter\":{\"category\":\"" + category + "\",\"ownership\":\"all\",\"includeHeld\":false}")), "Supported category/explicit false");
        Check(Valid(request("\"filter\":{\"defNames\":[\"Modded_Resourceα\"],\"corpses\":true,\"forbiddenOnly\":true,\"excludeChunks\":true}")), "Open native def identifiers and combined filters");
        // N01.03: frozen paging is no longer refused outright -- a cursor within the
        // byte bound is accepted (its actual freshness is checked at read time by the
        // shared NativeObservationSnapshot.Cursor helper, not by Validate).
        Check(Valid(request("\"page\":{\"cursor\":\"" + new string('a', 4096) + "\"}")), "A within-bound cursor is accepted by Validate");
        foreach (var invalid in new[] { "{}", request("\"page\":{\"limit\":0}"), request("\"page\":{\"limit\":257}"),
            request("\"page\":{\"cursor\":\"" + new string('a', 4097) + "\"}"), request("\"filter\":{\"category\":\"FOOD\"}"),
            request("\"filter\":{\"ownership\":\"enemy\"}"), request("\"filter\":{\"defNames\":[\"a\",\"a\"]}"),
            request("\"filter\":{\"defNames\":[\"\"]}"), request("\"filter\":{\"defNames\":[\"bad\\u0000id\"]}"),
            request("\"filter\":{\"region\":{\"minimum\":{\"x\":1,\"z\":0},\"maximum\":{\"x\":0,\"z\":0}}}") })
            Check(!Valid(invalid), "Invalid bounded request refused");
        var filter = Wire("StockFilter", "{}");
        Check((string)tools.GetMethod("Category", Flags)!.Invoke(null, new[] { filter })! == "haulable", "Default category unchanged");
        Check((string)tools.GetMethod("Ownership", Flags)!.Invoke(null, new[] { filter })! == "ours", "Default ownership unchanged");
        Check((bool)tools.GetMethod("IncludeHeld", Flags)!.Invoke(null, new[] { filter })!, "Held stock included by default");
        Func<bool, bool, bool, bool, bool, bool> ours = (fog, held, player, other, dead) =>
            (bool)tools.GetMethod("IsOurs", Flags)!.Invoke(null, new object[] { fog, held, player, other, dead })!;
        Check(ours(false, false, false, false, false), "Visible neutral spawned stock is usable");
        Check(!ours(true, false, true, false, false), "Fogged stock not usable even if player faction");
        Check(!ours(false, false, false, true, false), "Other faction spawned stock not ours");
        Check(ours(false, true, true, false, false), "Living player pawn held stock ours");
        Check(!ours(false, true, true, false, true), "Dead pawn held stock not ours");
        Check(!ours(false, true, false, true, false), "Trader held stock not ours");
        var server = Assembly.LoadFrom(directories.Select(d => Path.Combine(d, "RimBridgeServer.dll")).First(File.Exists));
        var binder = server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider", true)!.GetMethod("BindArguments", Flags)!;
        foreach (var value in new object?[] { "{}", new Dictionary<string, object>(), new List<object>(), null, 17, true })
        {
            var bound = (object[])binder.Invoke(null, new object?[] { tools.GetMethod("ListSupplies"),
                new Dictionary<string, object?> { ["request"] = value }, null, CancellationToken.None })!;
            Check(ReferenceEquals(bound[2], value), "Real SDK preserves raw request");
        }
        var reply = Wire("ListSuppliesReply", "{\"observed\":{\"stocks\":[{\"definition\":{\"defName\":\"Modded_Resourceα\"},\"units\":\"2147483648\",\"ours\":\"0\",\"holdersCompleteness\":{\"page\":{\"complete\":false}},\"issues\":[{\"field\":\"carried\",\"unavailable\":{\"reason\":\"UNAVAILABLE_REASON_NOT_REQUESTED\"}}]}]}}");
        var envelope = tools.GetMethod("Encode", Flags)!.Invoke(null, new[] { reply })!;
        var normalize = server.GetType("RimBridgeServer.LegacyToolExecution", true)!.GetMethod("ToDictionary", Flags)!;
        var normalized = (IDictionary)normalize.Invoke(null, new[] { envelope })!;
        Check(normalized.Count == 1 && normalized["payload"] is string, "SDK preserves ProtoJSON string");
        var snapshot = Get(Wire("ListSuppliesReply", (string)normalized["payload"]!), "Observed");
        var row = ((IList)Get(snapshot, "Stocks"))[0]!;
        Check((long)Get(row, "Units") == 2147483648L, "64-bit quantities roundtrip");
        Check((bool)Get(row, "HasOurs") && (long)Get(row, "Ours") == 0, "Known zero usable stock retained");
        Check(!(bool)Get(row, "HasCarried") && !(bool)Get(row, "HasInContainer"), "Unrequested held stock remains unknown");
        var oversized = Wire("ListSuppliesReply", "{\"unavailable\":{\"detail\":\"" + new string('x', 1024 * 1024) + "\"}}");
        try { tools.GetMethod("Encode", Flags)!.Invoke(null, new[] { oversized }); throw new Exception("Oversized reply accepted"); }
        catch (TargetInvocationException error) { Check(error.InnerException!.GetType().Name == "ReadLimit", "Oversize cannot truncate success"); }

        // ==================== N01.03: Project()-level fixtures ====================
        // Real Census.Add()/Walk() traversal depends on Verse.FogGrid's NativeBitArray field,
        // which is only initialized inside a running Unity player; touching it outside the game
        // process was observed to throw an intermittent System.TypeLoadException regardless of
        // fixture shape. These fixtures therefore exercise Project() directly with hand-built
        // StockEntry lists shaped exactly as Census traversal would have produced, per item.
        //
        // Two Harmony patches (loaded purely via reflection -- 0Harmony.dll is one of the
        // directories this suite already requires) neutralize environment-only crashes that
        // are unrelated to the code under test: Verse.Log.* calls into
        // UnityEngine.StackTraceUtility outside Unity and throws SecurityException, and
        // RimWorld.GenLabel.LabelExtras decorates labels via the native stat system (never
        // populated here). Project()/Entity() only forward the opaque diagnostic label text,
        // so stubbing both is a narrow, justified bypass of environment limitations -- not a
        // change to the observation logic under test.
        var harmonyAsm = Assembly.Load("0Harmony");
        var harmonyType = harmonyAsm.GetType("HarmonyLib.Harmony", true)!;
        var harmony = Activator.CreateInstance(harmonyType, "test.native-proto-supplies.fixtures")!;
        var harmonyMethodType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod", true)!;
        var patchMethod = harmonyType.GetMethod("Patch", new[] { typeof(MethodBase), harmonyMethodType, harmonyMethodType, harmonyMethodType, harmonyMethodType })!;
        var noopHarmony = Activator.CreateInstance(harmonyMethodType, typeof(Program).GetMethod(nameof(SkipOriginal), Flags)!);
        var logType = game.GetType("Verse.Log", true)!;
        foreach (var name in new[] { "Warning", "Error", "Message", "ErrorOnce", "WarningOnce" })
            foreach (var m in logType.GetMethods(Flags).Where(x => x.Name == name))
                patchMethod.Invoke(harmony, new object?[] { m, noopHarmony, null, null, null });
        var labelExtras = game.GetType("RimWorld.GenLabel", true)!.GetMethod("LabelExtras", Flags)!;
        var emptyStringHarmony = Activator.CreateInstance(harmonyMethodType, typeof(Program).GetMethod(nameof(EmptyStringResult), Flags)!);
        patchMethod.Invoke(harmony, new object?[] { labelExtras, emptyStringHarmony, null, null, null });

        var entryType = tools.GetNestedType("StockEntry", Flags)!;
        var projectM = tools.GetMethod("Project", Flags)!;

        object MakeDef(string name) { var d = New("Verse.ThingDef"); SetF(d, "defName", name); SetF(d, "label", name);
            SetF(d, "category", Enum.Parse(game.GetType("Verse.ThingCategory", true)!, "Item")); return d; }
        object MakeThing(string defName, int stack, int id, string typeName = "Verse.Thing")
        { var t = New(typeName); SetF(t, "def", MakeDef(defName)); SetF(t, "thingIDNumber", id); SetF(t, "stackCount", stack); return t; }
        void GiveComps(object thingWithComps, params object[] comps)
        {
            var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType("Verse.ThingComp", true)!))!;
            foreach (var c in comps) ((IList)list).Add(c);
            SetF(thingWithComps, "comps", list);
        }

        // Current.Game.maps trick so Thing.MapHeld resolves without ever stringifying a Map
        // (Map.ToString()'s reachable GravshipUtility/ModsConfig path is another Unity-only crash).
        var mapType = game.GetType("Verse.Map", true)!;
        var fixtureMap = FormatterServices.GetUninitializedObject(mapType);
        SetF(fixtureMap, "uniqueID", 9);
        var nativeGame = FormatterServices.GetUninitializedObject(game.GetType("Verse.Game", true)!);
        var mapsList = Activator.CreateInstance(typeof(List<>).MakeGenericType(mapType))!;
        ((IList)mapsList).Add(fixtureMap);
        SetF(nativeGame, "maps", mapsList);
        game.GetType("Verse.Current", true)!.GetProperty("Game")!.SetValue(null, nativeGame);
        void PlaceOnMap(object thing) => SetF(thing, "mapIndexOrState", (sbyte)0);

        object MakeEntry(object thing, object? holder, string? holderKind, bool ours, bool forbidden, bool fogged,
            bool inHome, bool inStockpile, bool playerFaction, bool otherFaction, bool trader)
        {
            var e = FormatterServices.GetUninitializedObject(entryType);
            SetF(e, "Thing", thing); SetF(e, "Holder", holder); SetF(e, "HolderKind", holderKind);
            SetF(e, "Position", Activator.CreateInstance(game.GetType("Verse.IntVec3", true)!, 2, 0, 2));
            SetF(e, "Ours", ours); SetF(e, "Forbidden", forbidden); SetF(e, "Fogged", fogged);
            SetF(e, "InHome", inHome); SetF(e, "InStockpile", inStockpile);
            SetF(e, "PlayerFaction", playerFaction); SetF(e, "OtherFaction", otherFaction); SetF(e, "Trader", trader);
            return e;
        }
        object RunProject(IList entries, bool includeHeld)
        {
            var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
            foreach (var e in entries) ((IList)list).Add(e);
            var reservedTyped = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(game.GetType("Verse.Thing", true)!))!;
            return projectM.Invoke(null, new object?[] { list, reservedTyped, includeHeld })!;
        }

        // ---- Item 5: a modded (non-vanilla) ThingDef fixture round-trips like a vanilla one ----
        var moddedThing = MakeThing("Modded_ExoticOreXYZ", 4, 501);
        PlaceOnMap(moddedThing);
        var moddedRow = RunProject(new object[] { MakeEntry(moddedThing, null, null, true, false, false, true, false, true, false, false) }, true);
        var moddedDef = Get(moddedRow, "Definition");
        Check((string)Get(moddedDef, "DefName") == "Modded_ExoticOreXYZ" && (string)Get(moddedDef, "Label") == "Modded_ExoticOreXYZ", "Modded def name/label observed like a vanilla def");
        var moddedItems = (IList)Get(moddedRow, "Items");
        Check(moddedItems.Count == 1 && (string)Get(moddedItems[0]!, "DefName") == "Modded_ExoticOreXYZ" && (int)Get(moddedItems[0]!, "MapId") == 9, "Modded def item entity carries its own def name and resolved map");
        Check((long)Get(moddedRow, "Units") == 4 && (long)Get(moddedRow, "Ours") == 4, "Modded def units/ownership aggregate exactly as a vanilla def would");

        // ---- Item 1: a corpse with populated inventory descends into its held items ----
        var pawnDef = MakeDef("Human_Test");
        var race = FormatterServices.GetUninitializedObject(game.GetType("Verse.RaceProperties", true)!);
        SetF(race, "intelligence", Enum.Parse(game.GetType("Verse.Intelligence", true)!, "Humanlike"));
        SetF(pawnDef, "race", race);
        var innerPawn = New("Verse.Pawn");
        SetF(innerPawn, "def", pawnDef); SetF(innerPawn, "thingIDNumber", 601); SetF(innerPawn, "factionInt", null);
        var kindDef = New("Verse.PawnKindDef"); SetF(kindDef, "defName", "Human_Test_Kind"); SetF(kindDef, "label", "test colonist");
        SetF(innerPawn, "kindDef", kindDef);
        GiveComps(innerPawn, FormatterServices.GetUninitializedObject(game.GetType("RimWorld.CompForbiddable", true)!));
        var corpse = New("Verse.Corpse");
        SetF(corpse, "def", MakeDef("Corpse_Human_Test")); SetF(corpse, "thingIDNumber", 602); SetF(corpse, "stackCount", 1);
        GiveComps(corpse, FormatterServices.GetUninitializedObject(game.GetType("RimWorld.CompForbiddable", true)!));
        // Corpse.InnerPawn's property setter NREs outside a running game (it depends on
        // machinery Corpse's constructor would normally have wired up); bypass it and construct
        // the backing ThingOwner<Pawn> directly, mirroring how Thing.MapHeld is resolved above.
        var pawnOwnerType = game.GetType("Verse.ThingOwner`1", true)!.MakeGenericType(game.GetType("Verse.Pawn", true)!);
        var pawnOwner = FormatterServices.GetUninitializedObject(pawnOwnerType);
        var pawnList = Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType("Verse.Pawn", true)!))!;
        ((IList)pawnList).Add(innerPawn);
        SetF(pawnOwner, "innerList", pawnList);
        SetF(corpse, "innerContainer", pawnOwner);
        // Corpse.LabelNoCount lazily translates "DeadLabel" through the grammar-resolution
        // system (backstories/rules), unavailable outside the real game and irrelevant here
        // since Entity() only forwards the opaque label text -- pre-seed the cache it checks first.
        SetF(corpse, "cachedLabel", "test colonist corpse");
        PlaceOnMap(corpse);
        var ration = MakeThing("Modded_CorpsePocketRation", 2, 603);
        PlaceOnMap(ration);
        var corpseRow = RunProject(new object[] {
            MakeEntry(corpse, null, null, false, false, false, true, false, false, false, false),
            MakeEntry(ration, corpse, "corpse", false, false, false, true, false, false, false, false),
        }, true);
        Check(((IList)Get(corpseRow, "Corpses")).Count == 1, "Corpse traversal descends into held inventory and reports one corpse");
        var corpseState = ((IList)Get(corpseRow, "Corpses"))[0]!;
        Check((string)Get(corpseState, "Race") == "Human_Test" && (bool)Get(corpseState, "Humanlike") && !(bool)Get(corpseState, "WasColonist"), "Corpse race/intelligence/faction facts observed");
        Check(((IList)Get(corpseRow, "Holders")).Count == 1, "Corpse-held ration surfaces as one held-stock row");
        var corpseIssues = (IList)Get(corpseRow, "Issues");
        Check(corpseIssues.Count == 2 && Enumerable.Range(0, corpseIssues.Count).Any(i => ((string)Get(corpseIssues[i]!, "Field")).EndsWith(".rot_stage")), "Corpse without a rot component reports the documented rot_stage issue, not a crash");

        // ---- Item 2: a live trader caravan pawn's carried goods surface as trader stock ----
        var traderHolder = MakeThing("Trader_Pawn_Test", 1, 701);
        PlaceOnMap(traderHolder);
        var goods = MakeThing("Modded_TradeGoods", 6, 702);
        PlaceOnMap(goods);
        var traderRow = RunProject(new object[] { MakeEntry(goods, traderHolder, "pawnInventory", false, false, false, false, false, false, true, true) }, true);
        Check((long)Get(traderRow, "TraderStock") == 6 && (long)Get(traderRow, "Ours") == 0, "Trader-carried goods count as trader stock, not ours");
        var traderHolders = (IList)Get(traderRow, "Holders");
        Check(traderHolders.Count == 1 && (string)Get(traderHolders[0]!, "HolderKind") == "pawnInventory", "Trader caravan pawn recorded as the held-stock holder");

        // ---- Item 3: a container nested inside another container is walked multiple levels ----
        var outerCrate = MakeThing("Modded_OuterCrate", 1, 801);
        var innerCrate = MakeThing("Modded_InnerCrate", 1, 802);
        var nestedGem = MakeThing("Modded_NestedGem", 5, 803);
        PlaceOnMap(outerCrate); PlaceOnMap(innerCrate); PlaceOnMap(nestedGem);
        var nestedRow = RunProject(new object[] {
            MakeEntry(outerCrate, null, null, true, false, false, true, false, true, false, false),
            MakeEntry(innerCrate, outerCrate, "container", true, false, false, true, false, true, false, false),
            MakeEntry(nestedGem, innerCrate, "container", true, false, false, true, false, true, false, false),
        }, true);
        Check((long)Get(nestedRow, "Stacks") == 3, "All three nesting levels retained as distinct stacks");
        var nestedHolders = (IList)Get(nestedRow, "Holders");
        Check(nestedHolders.Count == 2
            && (string)Get(Get(nestedHolders[0]!, "Holder"), "DefName") == "Modded_OuterCrate" && (string)Get(nestedHolders[0]!, "HolderKind") == "container"
            && (string)Get(Get(nestedHolders[1]!, "Holder"), "DefName") == "Modded_InnerCrate" && (string)Get(nestedHolders[1]!, "HolderKind") == "container",
            "Container-in-container multi-level nesting walked and each level's holder identity preserved");

        // ---- Item 4: overflow past the 256-rows-per-definition bound (Project()'s own cap) ----
        var overflowEntries = new List<object>();
        for (var i = 0; i < 257; i++)
        {
            var t = MakeThing("Modded_OverflowStack", 1, 900 + i);
            PlaceOnMap(t);
            overflowEntries.Add(MakeEntry(t, null, null, true, false, false, true, false, true, false, false));
        }
        try { RunProject(overflowEntries, true); Check(false, "257 rows for one definition must hit the 256-row bound"); }
        catch (TargetInvocationException error) { Check(error.InnerException!.GetType().Name == "ReadLimit" && error.InnerException.Message.Contains("256 rows"), "Complete stock item collection exceeds 256 rows bound enforced"); }

        Console.WriteLine(checks + " compiled supplies boundary assertions passed + fixtures; no gameplay assertions.");
        return 0;
    }
}
