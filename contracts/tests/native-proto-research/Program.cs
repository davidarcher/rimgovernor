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
    private static int Main(string[] args)
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
            "\"includeLocked\":false,\"nameContains\":\"\"", "\"page\":{\"limit\":256}", "\"nameContains\":\"Modded_α\"" })
            Check(Valid(request(good)), "Supported research request");
        foreach (var bad in new[] { "{}", request("\"page\":{\"limit\":0}"), request("\"page\":{\"limit\":257}"),
            request("\"page\":{\"cursor\":\"stale\"}"), request("\"nameContains\":\"bad\\u0000id\""), request("\"nameContains\":\"" + new string('α', 129) + "\"") })
            Check(!Valid(bad), "Invalid bounded research request refused");
        Refused(() => Wire("ResearchRequest", "{\"set\":\"Electricity\"}"), "Unknown mutation argument rejected by official parser");
        Refused(() => Wire("ResearchRequest", "{\"includeLocked\":\"false\"}"), "Nonboolean flag rejected");

        var native = Assembly.Load("Assembly-CSharp");
        var defType = native.GetType("Verse.ResearchProjectDef", true)!;
        var def = FormatterServices.GetUninitializedObject(defType);
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
        Console.WriteLine(checks + " compiled research boundary/read-only assertions passed; gameplay acceptance remains separate.");
        return 0;
    }
}
