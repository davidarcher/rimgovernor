#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

internal static class NativeProtoResearchProbe
{
    private static Assembly bridge = null!;
    private static Type tools = null!;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static object Wire(string name, string json)
    {
        var parser = bridge.GetType("RimGovernor.Protocol.Observations." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }
    private static object Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static object Call(string name, params object?[] args) => tools.GetMethod(name, Flags)!.Invoke(null, args)!;
    private static bool Valid(string json) => (bool)Call("Validate", Wire("ResearchRequest", json), null);
    private static void Refused(Action action, string label)
    {
        try { action(); } catch (TargetInvocationException) { Check(true, label); return; }
        throw new Exception(label);
    }
    internal static int Invoke(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools = bridge.GetType("HomeBridge.BridgeTools.NativeResearchObservationTools", true)!;
        const string scope = "\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string, string> request = fields => "{" + scope + (fields.Length == 0 ? "" : "," + fields) + "}";
        foreach (var good in new[] { "", "\"includeLocked\":true,\"includeFinished\":true,\"includeUnlocks\":true,\"includeCapability\":true",
            "\"includeLocked\":false,\"nameContains\":\"\"", "\"page\":{\"limit\":256}", "\"nameContains\":\"Modded_α\"",
            // N01.03: frozen paging is no longer refused outright -- a cursor within the
            // byte bound is accepted (its actual freshness is checked at read time by the
            // shared NativeObservationSnapshot.Cursor helper, not by Validate).
            "\"page\":{\"cursor\":\"stale\"}" })
            Check(Valid(request(good)), "Supported research request");
        foreach (var bad in new[] { "{}", request("\"page\":{\"limit\":0}"), request("\"page\":{\"limit\":257}"),
            request("\"page\":{\"cursor\":\"" + new string('x', 4097) + "\"}"), request("\"nameContains\":\"bad\\u0000id\""), request("\"nameContains\":\"" + new string('α', 129) + "\"") })
            Check(!Valid(bad), "Invalid bounded research request refused");
        Refused(() => Wire("ResearchRequest", "{\"set\":\"Electricity\"}"), "Unknown mutation argument rejected by official parser");
        Refused(() => Wire("ResearchRequest", "{\"includeLocked\":\"false\"}"), "Nonboolean flag rejected");

        var native = Assembly.Load("Assembly-CSharp");
        var defType = native.GetType("Verse.ResearchProjectDef", true)!;
        var def = FormatterServices.GetUninitializedObject(defType);
        defType.GetField("defName")!.SetValue(def, "Modded_Research");
        var unnamed = Call("Definition", def);
        Check(!(bool)Get(unnamed, "HasLabel") && (string)Get(unnamed, "DefName") == "Modded_Research", "Missing native label stays absent");
        defType.GetField("label")!.SetValue(def, "");
        Check((bool)Get(Call("Definition", def), "HasLabel"), "Explicit native empty label stays present");
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(defType, typeof(float));
        var progress = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        var knowledge = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        defType.GetField("baseCost")!.SetValue(def, 100f);
        for (var i = 0; i < 10; i++) Check((float)Call("Progress", def, progress, knowledge, true) == 0f, "Untouched project has native zero semantics");
        Check(progress.Count == 0 && knowledge.Count == 0, "Reading untouched projects never inserts saved rows");
        progress.Add(def, 125f); knowledge.Add(def, 12f);
        Check((float)Call("Progress", def, progress, knowledge, true) == 125f, "Ordinary progress source selected");
        defType.GetField("baseCost")!.SetValue(def, 0f); defType.GetField("knowledgeCost")!.SetValue(def, 50f);
        Check((float)Call("Progress", def, progress, knowledge, true) == 12f, "Anomaly source selected");
        Check((float)Call("Progress", def, progress, knowledge, false) == 0f, "Inactive DLC matches native no-progress rule");
        Check(progress.Count == 1 && knowledge.Count == 1, "Source dictionaries preserved");
        Check((bool)Call("Finished", 100d, 100d), "Completion boundary inclusive");
        Check(!(bool)Call("Finished", 99d, 100d), "Incomplete project preserved");
        Check((double)Call("Fraction", 125d, 100d) == 1d, "Native progress fraction clamped");
        Check((double)Call("Fraction", 25d, 100d) == .25d, "Fraction not percentage");
        foreach (var bad in new[] { -1d, double.NaN, double.PositiveInfinity })
            Refused(() => Call("Finished", bad, 100d), "Invalid native progress unavailable");
        Refused(() => Call("Fraction", 0d, 0d), "Undefined fraction cannot become fabricated zero");
        knowledge[def] = float.NaN;
        Refused(() => Call("Progress", def, progress, knowledge, true), "Corrupt stored progress rejected");

        var server = Assembly.LoadFrom(directories.Select(d => Path.Combine(d, "RimBridgeServer.dll")).First(File.Exists));
        var binder = server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider", true)!.GetMethod("BindArguments", Flags)!;
        foreach (var value in new object?[] { "{}", new Dictionary<string, object>(), new List<object>(), null, 17, true })
        {
            var bound = (object[])binder.Invoke(null, new object?[] { tools.GetMethod("ReadResearch"),
                new Dictionary<string, object?> { ["request"] = value }, null, CancellationToken.None })!;
            Check(ReferenceEquals(bound[2], value), "SDK preserves raw request for strict boundary");
        }
        var envelope = (IDictionary)Call("Encode", Wire("ResearchReply", "{\"observed\":{\"projects\":[{\"progress\":0,\"finished\":false,\"canStart\":false}],\"slots\":[{}]}}"));
        var observed = Get(Wire("ResearchReply", (string)envelope["payload"]!), "Observed");
        var row = ((IList)Get(observed, "Projects"))[0]!;
        Check((bool)Get(row, "HasProgress") && (double)Get(row, "Progress") == 0, "Known zero progress retained");
        Check((bool)Get(row, "HasCanStart") && !(bool)Get(row, "CanStart"), "Known false eligibility retained");
        Check(Get(observed, "Snapshot") == null, "No fabricated CAS token");
        var oversized = (IDictionary)Call("Encode", Wire("ResearchReply", "{\"unavailable\":{\"detail\":\"" + new string('x', 1024 * 1024) + "\"}}"));
        Check(Get(Wire("ResearchReply", (string)oversized["payload"]!), "Unavailable") != null, "Oversized reply refuses instead of truncating facts");

        // N01.03: Capability() (bench/facility/researcher projection) and Read()'s Anomaly
        // path (ModsConfig.AnomalyActive, KnowledgeCategoryProject, EntityCodex.Hidden) cannot
        // be reflection-invoked in this net472 harness at all -- this is not specific to the
        // Anomaly DLC being absent. Every entry point touches a RimWorld static whose own type
        // initializer calls into Unity native (ecall) methods that .NET Framework refuses
        // outside the Unity-hosted CLR ("ECall methods must be packaged into a system module"),
        // and a failed type initializer poisons that type for the rest of the process. Verified
        // directly against the installed game assemblies:
        //   - WorkTypeDefOf/SkillDefOf..cctor -> DefOfHelper.EnsureInitializedInCtor ->
        //     Verse.Log.Warning -> UnityEngine.StackTraceUtility.ExtractStackTrace (ecall).
        //     Capability() references WorkTypeDefOf.Research/SkillDefOf.Intellectual
        //     unconditionally, so it cannot run even with zero benches/pawns.
        //   - NativeBuildingObservationTools.Project(Thing) reads thing.LabelCap, which reaches
        //     RimWorld.GenLabel -> RimWorld.StatDefOf..cctor (same ecall failure), so no real
        //     spawned Building can be projected -- consistent with native-proto-buildings' own
        //     tests never calling Project() either.
        //   - RimWorld.CompAffectedByFacilities..cctor -> Verse.GenDraw..cctor (ecall), so the
        //     type cannot even be constructed via FormatterServices.GetUninitializedObject
        //     without the static constructor already having run and failed.
        //   - Verse.ModsConfig..cctor -> Verse.GenFilePaths.get_SaveDataFolderPath (ecall), so
        //     Read()'s very first statement (`ModsConfig.AnomalyActive`) cannot execute, which
        //     also makes EntityCodex.Hidden (it reads ModsConfig.AnomalyActive internally)
        //     unreachable regardless of whether Anomaly's KnowledgeCategoryProject/EntityCodex
        //     types are otherwise loadable (they are).
        // What would be needed: hosting inside the actual RimWorld/Unity process, i.e. a native
        // scenario/game acceptance test, matching this file's README ("native acceptance must
        // compare ... bench, facility and researcher facts against actual game state").
        // Substitute coverage below exercises this session's actual new surface -- Token's exact
        // field selection and QuerySeed's exact field selection -- which are pure managed code
        // reachable from this harness.
        var identityType = bridge.GetType("RimGovernor.Protocol.Common.Identity", true)!;
        var identity = Activator.CreateInstance(identityType)!;
        identityType.GetProperty("ColonyId")!.SetValue(identity, "colony");
        identityType.GetProperty("LoadToken")!.SetValue(identity, "load");
        identityType.GetProperty("MapId")!.SetValue(identity, 0);
        var contextType = bridge.GetType("RimGovernor.Protocol.Common.ObservationContext", true)!;
        var context = Activator.CreateInstance(contextType)!;
        contextType.GetProperty("Identity")!.SetValue(context, identity);
        object Snapshot(string json) => Wire("ResearchSnapshot", json);
        string TokenOf(object snapshot) => (string)Get(Call("Token", context, snapshot), "Token");
        const string baseline = "{\"anomalyActive\":false,\"playerTechLevel\":\"Neolithic\"," +
            "\"slots\":[{},{\"category\":\"Basic\",\"currentProject\":\"Alpha\"}]," +
            "\"projects\":[{\"project\":{\"defName\":\"Alpha\"},\"progress\":1,\"finished\":false,\"current\":true}]}";
        var baselineToken = TokenOf(Snapshot(baseline));
        Check(baselineToken == TokenOf(Snapshot(baseline)), "Identical research snapshot content yields identical CAS token");
        Check(baselineToken != TokenOf(Snapshot(baseline.Replace("\"anomalyActive\":false", "\"anomalyActive\":true"))),
            "AnomalyActive change invalidates research CAS token");
        Check(baselineToken != TokenOf(Snapshot(baseline.Replace("Neolithic", "Industrial"))),
            "PlayerTechLevel change invalidates research CAS token");
        Check(baselineToken != TokenOf(Snapshot(baseline.Replace("\"currentProject\":\"Alpha\"", "\"currentProject\":\"Beta\""))),
            "Selected slot project change invalidates research CAS token");
        Check(baselineToken != TokenOf(Snapshot(baseline.Replace("\"progress\":1", "\"progress\":2"))),
            "Project progress change invalidates research CAS token");
        Check(baselineToken != TokenOf(Snapshot(baseline.Replace("\"finished\":false", "\"finished\":true"))),
            "Project finished change invalidates research CAS token");
        Check(baselineToken != TokenOf(Snapshot(baseline.Replace("\"current\":true", "\"current\":false"))),
            "Project current-selection change invalidates research CAS token");
        var withCompleteness = baseline.TrimEnd('}') + ",\"completeness\":{\"matched\":\"9\",\"returned\":\"9\"}}";
        Check(baselineToken == TokenOf(Snapshot(withCompleteness)), "Token is scoped to documented fields only, not the whole reply");
        var tokenRef = Call("Token", context, Snapshot(baseline));
        Check((string)Get(tokenRef, "EntityId") == "research-manager", "Research CAS token is scoped to the research manager entity");
        var returnedContext = Get(tokenRef, "Context");
        Check(!ReferenceEquals(returnedContext, context) && (string)Get(Get(returnedContext, "Identity"), "ColonyId") == "colony",
            "Research CAS token clones the caller's context rather than aliasing it");
        Func<string, object> seedRequest = fields => Wire("ResearchRequest", request(fields));
        var defaultSeed = Call("QuerySeed", seedRequest(""));
        Check(defaultSeed.Equals(Call("QuerySeed", seedRequest(""))), "Identical research query produces identical paging seed");
        foreach (var fields in new[] { "\"includeLocked\":true", "\"includeFinished\":true", "\"includeUnlocks\":true",
            "\"includeCapability\":true", "\"nameContains\":\"Alpha\"" })
            Check(!defaultSeed.Equals(Call("QuerySeed", seedRequest(fields))), "Changed research filter invalidates paging seed: " + fields);

        Console.WriteLine(checks + " compiled research boundary/read-only assertions passed; gameplay acceptance remains separate.");
        return 0;
    }
}
