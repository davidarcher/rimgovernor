using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HomeBridge.BridgeTools;
using RimBridgeServer.Sdk;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }

    private static void Main()
    {
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "RimBridgeServer"),
            "probe must start in a fresh process without the real server assembly");
        var context = new Context("operation-a");
        string unavailable;
        Check(BridgeCommon.RawArguments(context, out unavailable) == null && unavailable != null,
            "lookup before server assembly loads must report unavailable");
        Check(BridgeCommon.RawArguments(context, out unavailable) == null && unavailable != null,
            "repeated early miss remains unavailable");

        // The tested production reflection must discover this later-loaded internal
        // type. Do not reset private caches or replace the production lookup.
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("RimBridgeServer"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("JournalProbe");
        var type = module.DefineType("RimBridgeServer.RimBridgeCapabilities",
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var field = type.DefineField("CurrentJournal", typeof(object), FieldAttributes.Public | FieldAttributes.Static);
        var getter = type.DefineMethod("get_Journal", MethodAttributes.Public | MethodAttributes.Static |
            MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(object), Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ret);
        var property = type.DefineProperty("Journal", PropertyAttributes.None, typeof(object), null);
        property.SetGetMethod(getter);
        var capabilities = type.CreateType();
        Check(!capabilities.IsPublic && capabilities.GetProperty("Journal").GetGetMethod().IsPublic,
            "fake server matches internal type/public static property boundary");

        var arguments = new Dictionary<string, object> { ["request"] = "{}", ["unknown"] = 7 };
        var journal = new Journal("operation-a", arguments);
        capabilities.GetField("CurrentJournal").SetValue(null, journal);
        Check(ReferenceEquals(BridgeCommon.RawArguments(context, out unavailable), arguments) && unavailable == null,
            "early miss must not poison lookup after server loads");
        Check(journal.Calls == 1 && journal.LastId == "operation-a" && journal.LastIncludePayload == false,
            "journal read uses exact operation id and false payload flag");
        Check(arguments.Count == 2 && (int)arguments["unknown"] == 7,
            "raw arguments preserve unknown keys without mutation");
        Check(ReferenceEquals(BridgeCommon.RawArguments(context, out unavailable), arguments) && journal.Calls == 2,
            "positive property cache still reads current operation");
        Check(BridgeCommon.RawArguments(new Context("other-operation"), out unavailable) == null && unavailable != null,
            "missing operation cannot reuse prior raw arguments");
        var replacement = new Dictionary<string, object> { ["request"] = "{\"new\":true}" };
        capabilities.GetField("CurrentJournal").SetValue(null, new Journal("operation-a", replacement));
        Check(ReferenceEquals(BridgeCommon.RawArguments(context, out unavailable), replacement),
            "property cache does not cache journal instance or argument values");
        Console.WriteLine("Journal cache regression passed: " + checks +
            " checks; production BridgeCommon, dynamically loaded fake server, no game execution.");
    }

    private sealed class Context : IRimBridgeContext
    {
        internal Context(string operationId) { OperationId = operationId; }
        public string OperationId { get; }
        public string CapabilityId => "journal-cache-probe";
        public IRimBridgeToolClient Tools => throw new InvalidOperationException("No tool invocation");
        public IRimBridgeGameClock Game => throw new InvalidOperationException("No game execution");
        public IRimBridgeMainThread MainThread => throw new InvalidOperationException("No main-thread scheduling");
    }
}

public sealed class Journal
{
    private readonly string id;
    private readonly Envelope envelope;
    public int Calls;
    public string LastId;
    public bool LastIncludePayload;
    public Journal(string id, IDictionary<string, object> arguments)
    {
        this.id = id;
        envelope = new Envelope { Metadata = new Dictionary<string, object> { ["arguments"] = arguments } };
    }
    public Envelope GetOperation(string operationId, bool includePayload)
    {
        Calls++;
        LastId = operationId;
        LastIncludePayload = includePayload;
        return operationId == id ? envelope : null;
    }
}
public sealed class Envelope { public IDictionary<string, object> Metadata { get; set; } }
