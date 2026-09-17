using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using RimGovernor.Protocol.Placement;
using Shared = RimGovernor.Protocol.Common;
using Control = RimGovernor.Protocol.Authority;

internal static class Program
{
    private static int checks;
    private static readonly List<string> manifest = new List<string> { "id\tmessage\tjson\tbinary" };
    private static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RoundTrip<T>(T message, MessageParser<T> parser, string name, string output)
        where T : IMessage<T>
    {
        var json = JsonFormatter.Default.Format(message);
        Require(message.Equals(parser.ParseJson(json)), name + " ProtoJSON round trip");
        Require(message.Equals(parser.ParseFrom(message.ToByteArray())), name + " binary round trip");
        File.WriteAllText(Path.Combine(output, name + ".json"), json);
        File.WriteAllBytes(Path.Combine(output, name + ".bin"), message.ToByteArray());
    }

    private static void Refuse(Action operation, string message)
    {
        try { operation(); }
        catch (InvalidProtocolBufferException) { checks++; return; }
        throw new InvalidOperationException(message);
    }

    private static FileDescriptor[] CanonicalFiles()
    {
        // Discover generated descriptors, never parse .proto or generate source.
        return typeof(PlacementRequest).Assembly.GetTypes()
            .Select(t => t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static))
            .Where(p => p?.PropertyType == typeof(FileDescriptor))
            .Select(p => (FileDescriptor)p!.GetValue(null)!)
            .Where(d => d.Package.StartsWith("rimgovernor.", StringComparison.Ordinal))
            .OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<MessageDescriptor> Messages(MessageDescriptor descriptor)
    {
        if (descriptor.ClrType != null) yield return descriptor;
        foreach (var nested in descriptor.NestedTypes)
            foreach (var message in Messages(nested)) yield return message;
    }

    private static Dictionary<string, MessageDescriptor> CanonicalMessages()
    {
        return CanonicalFiles().SelectMany(f => f.MessageTypes).SelectMany(Messages)
            .ToDictionary(d => d.FullName, d => d, StringComparer.Ordinal);
    }

    private static object Scalar(FieldDescriptor field, int variant)
    {
        switch (field.FieldType)
        {
            case FieldType.Bool: return variant != 0;
            case FieldType.String: return variant == 0 ? "" : "fixture-\u03bb-\ud83c\udf31";
            case FieldType.Bytes: return variant == 0 ? ByteString.Empty : ByteString.CopyFrom(new byte[] { 0, 1, 127, 255 });
            case FieldType.Int32: case FieldType.SInt32: case FieldType.SFixed32:
                return variant == 0 ? 0 : variant == 2 ? int.MinValue : variant == 3 ? int.MaxValue : 17;
            case FieldType.UInt32: case FieldType.Fixed32:
                return variant == 0 ? 0U : variant == 3 ? uint.MaxValue : 17U;
            case FieldType.Int64: case FieldType.SInt64: case FieldType.SFixed64:
                return variant == 0 ? 0L : variant == 2 ? long.MinValue : variant == 3 ? long.MaxValue : 9007199254740993L;
            case FieldType.UInt64: case FieldType.Fixed64:
                return variant == 0 ? 0UL : variant == 3 ? ulong.MaxValue : 9007199254740993UL;
            case FieldType.Float: return variant == 0 ? 0f : variant == 2 ? -float.MaxValue : variant == 3 ? float.MaxValue : 1.25f;
            case FieldType.Double: return variant == 0 ? 0d : variant == 2 ? -double.MaxValue : variant == 3 ? double.MaxValue : 1.25d;
            case FieldType.Enum:
                var values = field.EnumType.Values;
                return System.Enum.ToObject(field.EnumType.ClrType, values[variant == 0 ? 0 : values.Count - 1].Number);
            default: throw new InvalidOperationException("Unsupported scalar " + field.FullName);
        }
    }

    private static object Value(FieldDescriptor field, int variant, int depth, HashSet<string> ancestors)
    {
        return field.FieldType == FieldType.Message
            ? Populate(field.MessageType, variant, depth, ancestors) : Scalar(field, variant);
    }

    private static IMessage Populate(MessageDescriptor descriptor, int variant, int depth, HashSet<string> ancestors)
    {
        var message = descriptor.Parser.ParseFrom(Array.Empty<byte>());
        if (depth >= 3 || ancestors.Contains(descriptor.FullName)) return message;
        var path = new HashSet<string>(ancestors, StringComparer.Ordinal) { descriptor.FullName };
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (field.RealContainingOneof != null && field.RealContainingOneof.Fields[0] != field) continue;
            if (field.IsMap)
            {
                var dictionary = (IDictionary)field.Accessor.GetValue(message);
                var key = field.MessageType.FindFieldByNumber(1);
                var value = field.MessageType.FindFieldByNumber(2);
                dictionary.Add(Scalar(key, 1), Value(value, variant, depth + 1, path));
            }
            else if (field.IsRepeated)
            {
                var list = (IList)field.Accessor.GetValue(message);
                list.Add(Value(field, variant, depth + 1, path));
                list.Add(Value(field, variant == 0 ? 1 : 0, depth + 1, path));
            }
            else field.Accessor.SetValue(message, Value(field, variant, depth + 1, path));
        }
        return message;
    }

    private static void Emit(IMessage message, string id, string output)
    {
        Require(!string.IsNullOrEmpty(id) && id.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.'), "Safe fixture identifier");
        Require(!File.Exists(Path.Combine(output, id + ".json")) && !File.Exists(Path.Combine(output, id + ".bin")),
            "Fixture identifier does not overwrite an independent sample: " + id);
        var json = JsonFormatter.Default.Format(message);
        var binary = message.ToByteArray();
        Require(message.Equals(message.Descriptor.Parser.ParseJson(json)), id + " reflected ProtoJSON round trip");
        Require(message.Equals(message.Descriptor.Parser.ParseFrom(binary)), id + " reflected binary round trip");
        File.WriteAllText(Path.Combine(output, id + ".json"), json, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(output, id + ".bin"), binary);
        manifest.Add(id + "\t" + message.Descriptor.FullName + "\t" + id + ".json\t" + id + ".bin");
    }

    private static void FullPackage(Dictionary<string, MessageDescriptor> descriptors, string output)
    {
        int index = 0;
        foreach (var descriptor in descriptors.Values.OrderBy(d => d.FullName, StringComparer.Ordinal))
        {
            Action<IMessage> emit = m => Emit(m, "csharp-shape-" + (++index).ToString("D5"), output);
            emit(descriptor.Parser.ParseFrom(Array.Empty<byte>()));
            for (int variant = 0; variant < 4; variant++)
                emit(Populate(descriptor, variant, 0, new HashSet<string>(StringComparer.Ordinal)));
            foreach (var oneof in descriptor.Oneofs.Where(o => !o.IsSynthetic))
            {
                foreach (var field in oneof.Fields)
                {
                    var message = Populate(descriptor, 1, 0, new HashSet<string>(StringComparer.Ordinal));
                    field.Accessor.SetValue(message, Value(field, 1, 1, new HashSet<string>(StringComparer.Ordinal)));
                    Require(oneof.Accessor.GetCaseFieldDescriptor(message) == field, field.FullName + " selected oneof");
                    foreach (var other in oneof.Fields.Where(f => f != field))
                        Require(!other.Accessor.HasValue(message), other.FullName + " cleared competing oneof");
                    emit(message);
                }
            }
            foreach (var field in descriptor.Fields.InFieldNumberOrder().Where(f => f.FieldType == FieldType.Enum))
            {
                foreach (var value in field.EnumType.Values)
                {
                    var message = descriptor.Parser.ParseFrom(Array.Empty<byte>());
                    var enumValue = System.Enum.ToObject(field.EnumType.ClrType, value.Number);
                    if (field.IsRepeated) ((IList)field.Accessor.GetValue(message)).Add(enumValue);
                    else field.Accessor.SetValue(message, enumValue);
                    emit(message);
                }
            }
        }
        File.WriteAllLines(Path.Combine(output, "descriptor-files.txt"), CanonicalFiles().Select(d => d.Name));
        Console.WriteLine("Reflected " + descriptors.Count + " concrete canonical messages; emitted " + index + " serialization shapes (not domain-admission fixtures).");
    }

    private static void Example(Dictionary<string, MessageDescriptor> descriptors, string type, string json, string id, string output)
    {
        if (!descriptors.TryGetValue(type, out var descriptor))
            throw new InvalidOperationException("Required canonical fixture type missing: " + type);
        Emit(descriptor.Parser.ParseJson(json), id, output);
    }

    private static void FamilyExamples(Dictionary<string, MessageDescriptor> descriptors, string output)
    {
        Example(descriptors, "rimgovernor.authority.v1.ControlRequest",
            @"{""setMode"":{""identity"":{""colonyId"":""colony"",""loadToken"":""load"",""mapId"":0},""expectedGeneration"":""18446744073709551615"",""mode"":""MODE_AUTO""}}",
            "csharp-authority-boundary", output);
        Example(descriptors, "rimgovernor.clock.v1.ControlReceipt",
            @"{""attempt"":{""controllerSessionId"":""session"",""actionId"":""clock"",""attemptId"":""1""},""admittedContext"":{""identity"":{""colonyId"":""colony"",""loadToken"":""load"",""mapId"":0},""tick"":""0"",""nativeGeneration"":""1""},""uncertain"":{""detail"":""Clock effect requires fresh inspection""}}",
            "csharp-clock-uncertain", output);
        Example(descriptors, "rimgovernor.lifecycle.v1.LoadReply",
            @"{""pending"":{""requestId"":""load-request"",""saveName"":""save"",""processConnected"":true,""mapReady"":false,""visualReady"":false}}",
            "csharp-load-pending", output);
        Example(descriptors, "rimgovernor.operations.v1.ExecuteRequest",
            @"{""precondition"":{""identity"":{""colonyId"":""colony"",""loadToken"":""load"",""mapId"":0},""expectedGeneration"":""1"",""attempt"":{""controllerSessionId"":""session"",""actionId"":""settings"",""attemptId"":""1""}},""operation"":{""patchPawn"":{""pawn"":{""entityId"":""Thing_Pawn1"",""expectedSnapshotToken"":""snapshot""},""selfTend"":false}}}",
            "csharp-operation-present-false", output);
        Example(descriptors, "rimgovernor.receipts.v1.Receipt",
            @"{""attempt"":{""controllerSessionId"":""session"",""actionId"":""settings"",""attemptId"":""1""},""admittedContext"":{""identity"":{""colonyId"":""colony"",""loadToken"":""load"",""mapId"":0},""tick"":""42"",""nativeGeneration"":""1""},""applied"":{""observed"":{""settings"":{""snapshot"":{""entityId"":""Thing_Pawn1"",""beforeToken"":""before"",""afterToken"":""after""}}}}}",
            "csharp-attributed-settings", output);
        Refuse(() => Shared.AttemptKey.Parser.ParseJson(@"{""attemptId"":""18446744073709551616""}"), "Overflow uint64 accepted");
        Refuse(() => Shared.AttemptKey.Parser.ParseJson(@"{""attemptId"":""-1""}"), "Negative uint64 accepted");
        Refuse(() => Shared.Identity.Parser.ParseFrom(new byte[] { 10, 5, 65 }), "Truncated binary string accepted");
        Refuse(() => Shared.Identity.Parser.ParseFrom(new byte[] { 10, 1, 255 }), "Invalid binary UTF8 accepted");
        Refuse(() => Shared.Identity.Parser.ParseJson(@"{""mapId"":1.5}"), "Fractional integer accepted");
        Refuse(() => Shared.Identity.Parser.ParseJson(@"{""mapId"":""NaN""}"), "NaN integer accepted");
        Refuse(() => Control.ControlRequest.Parser.ParseJson(@"{""setMode"":{},""revoke"":{}}"), "Multiple real oneof arms accepted");
        Refuse(() => Control.ControlRequest.Parser.ParseJson(@"{""acquire"":{}}"), "Removed owner-lease acquire arm accepted");
        var nullMap = Shared.Identity.Parser.ParseJson(@"{""mapId"":null}");
        Require(!nullMap.HasMapId, "ProtoJSON null leaves optional scalar absent");
        var numeric = Shared.AttemptKey.Parser.ParseJson(@"{""attemptId"":""1e2""}");
        Require(numeric.AttemptId == 100, "Official ProtoJSON exponent spelling is serialization-valid");
        var unknownBinary = Shared.Identity.Parser.ParseFrom(new byte[] { 160, 6, 1 });
        Require(unknownBinary.ToByteArray().SequenceEqual(new byte[] { 160, 6, 1 }), "Unknown binary fields are retained; mutation admission must reject them separately");
    }

    private static string FixturePath(string directory, string file)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file || file.Contains("/") || file.Contains("\\"))
            throw new InvalidOperationException("Manifest fixture path must be a filename");
        return Path.Combine(directory, file);
    }

    private static void IncomingManifest(Dictionary<string, MessageDescriptor> descriptors, string incoming, string output)
    {
        var path = Path.Combine(incoming, "manifest.tsv");
        if (!File.Exists(path)) throw new InvalidOperationException("Full-package exchange requires manifest.tsv");
        var lines = File.ReadAllLines(path);
        Require(lines.Length > 1 && lines[0] == "id\tmessage\tjson\tbinary", "Cross-language manifest header");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split('\t');
            Require(parts.Length == 4 && ids.Add(parts[0]), "Unique manifest fixture ID and four columns");
            // A Go exchange directory also retains echoes of C# origins. Those
            // are verified by VerifyReturn, not turned into second-generation echoes.
            if (parts[0].StartsWith("csharp-echo-", StringComparison.Ordinal)) continue;
            if (!descriptors.TryGetValue(parts[1], out var descriptor))
                throw new InvalidOperationException("Incoming unrecognized canonical message: " + parts[1]);
            var json = descriptor.Parser.ParseJson(File.ReadAllText(FixturePath(incoming, parts[2])));
            var binary = descriptor.Parser.ParseFrom(File.ReadAllBytes(FixturePath(incoming, parts[3])));
            Require(json.Equals(binary), "Go ProtoJSON/binary agree: " + parts[0]);
            Emit(json, "go-echo-" + parts[0], output);
        }
    }

    private static Dictionary<string, string[]> ReadManifest(string directory, string filename)
    {
        var lines = File.ReadAllLines(Path.Combine(directory, filename));
        Require(lines.Length > 1 && lines[0] == "id\tmessage\tjson\tbinary", filename + " header and nonempty fixtures");
        var records = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split('\t');
            Require(fields.Length == 4 && !string.IsNullOrWhiteSpace(fields[0]), filename + " four columns and ID");
            Require(!records.ContainsKey(fields[0]), filename + " duplicate ID: " + fields[0]);
            records.Add(fields[0], fields);
        }
        return records;
    }

    private static IMessage ReadFixture(Dictionary<string, MessageDescriptor> descriptors, string directory, string[] record)
    {
        if (!descriptors.TryGetValue(record[1], out var descriptor))
            throw new InvalidOperationException("Unknown canonical fixture message: " + record[1]);
        var json = descriptor.Parser.ParseJson(File.ReadAllText(FixturePath(directory, record[2])));
        var binary = descriptor.Parser.ParseFrom(File.ReadAllBytes(FixturePath(directory, record[3])));
        Require(json.Equals(binary), "Manifest JSON/binary agreement: " + record[0]);
        return json;
    }

    private static void VerifyReturn(string originDirectory, string returnDirectory)
    {
        const string prefix = "csharp-echo-";
        var descriptors = CanonicalMessages();
        var original = ReadManifest(originDirectory, "manifest.tsv");
        foreach (var id in original.Keys.Where(id => id.StartsWith("go-echo-", StringComparison.Ordinal)).ToArray())
            original.Remove(id);
        var returned = ReadManifest(returnDirectory, "csharp-echo-manifest.tsv");
        Require(original.Count == returned.Count, "Return manifest must cover the exact complete original set");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in returned.Values)
        {
            Require(record[0].StartsWith(prefix, StringComparison.Ordinal), "Return fixture requires exactly one csharp-echo prefix");
            var originId = record[0].Substring(prefix.Length);
            if (!original.TryGetValue(originId, out var source))
                throw new InvalidOperationException("Unexpected returned fixture: " + record[0]);
            Require(seen.Add(originId), "Returned original ID is unique");
            Require(source[1] == record[1], "Returned fully qualified message type matches: " + originId);
            var expected = ReadFixture(descriptors, originDirectory, source);
            var actual = ReadFixture(descriptors, returnDirectory, record);
            Require(expected.Equals(actual), "Returned value matches original including optional presence: " + originId);
        }
        Require(seen.SetEquals(original.Keys), "No original fixture omitted from reciprocal return");
        Console.WriteLine("Verified reciprocal return against " + original.Count + " original manifest fixtures: " + checks + " checks.");
    }

    public static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception error)
        {
            // A failed proof must exit promptly rather than invoke Windows crash reporting.
            Console.Error.WriteLine(error.ToString());
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "--verify-return")
        {
            VerifyReturn(args[1], args[2]);
            return;
        }
        if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Supply a fresh proof artifact directory");
        var output = args[0];
        if (Directory.Exists(output)) throw new ArgumentException("Proof artifact directory must be fresh");
        Directory.CreateDirectory(output);
        var candidate = new PlacementCandidate { DefName = "Wall", X = 0, Z = int.MaxValue,
            Rotation = Rotation.North, Stuff = "" };
        var request = new PlacementRequest { Identity = new Shared.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 } };
        request.Placements.Add(candidate);
        RoundTrip(request, PlacementRequest.Parser, "csharp-request", output);
        Require(candidate.HasX && candidate.HasStuff, "Explicit zero and empty string retain presence");
        var absent = PlacementRequest.Parser.ParseJson("{\"placements\":[{}]}").Placements[0];
        Require(!absent.HasX && !absent.HasRotation && !absent.HasDefName && !absent.HasStuff,
            "Absent request fields remain absent for semantic validation");
        var missing = new PlacementMaterialStock { DefName = "WoodLog" };
        var empty = new PlacementMaterialStock { DefName = "WoodLog", Available = 0 };
        Require(!missing.HasAvailable && empty.HasAvailable, "Unknown and known-empty stock differ");
        var evaluated = new PlacementEvaluated { CanPlace = false, MadeFromStuff = true,
            Passability = Passability.Impassable, IsDoor = false, ResearchFinished = true,
            BuildableByPlayer = true, Materials = new PlacementMaterials { Known = new MaterialRows() } };
        evaluated.Materials.Known.Rows.Add(missing);
        evaluated.Materials.Known.Rows.Add(empty);
        evaluated.CostList.Add(new PlacementCost { DefName = "WoodLog", Count = 5 });
        var rotation = new PlacementRotation { Rotation = Rotation.North, Accepted = false, Reason = "Blocked" };
        rotation.OccupiedCells.Add(new Shared.Cell { X = int.MinValue, Z = 0 });
        rotation.BlockingThings.Add(new PlacementBlocker { Category = "Building", IsBlueprint = false,
            IsFrame = false, WouldBeWiped = false, FrameWouldBeCancelled = false });
        evaluated.Rotations.Add(rotation);
        var batch = new PlacementBatch { Context = new Shared.ObservationContext { Identity = request.Identity, Tick = 0, NativeGeneration = 1 } };
        batch.Results.Add(new CandidateReply { Evaluated = evaluated });
        batch.Results.Add(new CandidateReply { Failure = new Shared.Failure { Code = Shared.FailureCode.NotFound, Detail = "Unknown definition" } });
        var reply = new PlacementReply { Batch = batch };
        RoundTrip(reply, PlacementReply.Parser, "csharp-reply", output);
        var decoded = PlacementReply.Parser.ParseJson(JsonFormatter.Default.Format(reply));
        Require(decoded.OutcomeCase == PlacementReply.OutcomeOneofCase.Batch, "Batch oneof arm retained");
        Require(decoded.Batch.Results[0].OutcomeCase == CandidateReply.OutcomeOneofCase.Evaluated,
            "Evaluated candidate retained");
        Require(decoded.Batch.Results[0].Evaluated.HasCanPlace && !decoded.Batch.Results[0].Evaluated.CanPlace,
            "Present false is a known refusal");
        Require(!decoded.Batch.Results[0].Evaluated.Materials.Known.Rows[0].HasAvailable
            && decoded.Batch.Results[0].Evaluated.Materials.Known.Rows[1].HasAvailable, "Stock presence round trip");
        Require(decoded.Batch.Results[1].OutcomeCase == CandidateReply.OutcomeOneofCase.Failure,
            "Candidate semantic failure remains separate from batch failure");
        var failure = new PlacementReply { Failure = new Shared.Failure { Code = Shared.FailureCode.InvalidRequest, Detail = "Invalid request" } };
        RoundTrip(failure, PlacementReply.Parser, "failure", output);
        reply.Failure = failure.Failure;
        Require(reply.Batch == null && reply.OutcomeCase == PlacementReply.OutcomeOneofCase.Failure,
            "Setting oneof arm clears previous arm");
        Require(new PlacementReply().OutcomeCase == PlacementReply.OutcomeOneofCase.None,
            "Absent reply variant remains detectable by semantic validator");
        Refuse(() => PlacementRequest.Parser.ParseJson("{\"unknown\":true}"), "Unknown ProtoJSON field accepted");
        Refuse(() => PlacementRequest.Parser.ParseJson("{\"placements\":[{\"x\":2147483648}]}"), "Out-of-range coordinate accepted");
        var unknownEnum = PlacementRequest.Parser.ParseJson("{\"placements\":[{\"rotation\":99}]}");
        Require((int)unknownEnum.Placements[0].Rotation == 99,
            "Proto3 unknown enum values need ordinary semantic validation");
        var maximum = new UInt64Value { Value = ulong.MaxValue };
        Require(JsonFormatter.Default.Format(maximum) == "\"18446744073709551615\"", "ProtoJSON uint64 is exact decimal string");
        RoundTrip(maximum, UInt64Value.Parser, "csharp-u64", output);
        var context = new Shared.ObservationContext {
            Identity = new Shared.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 },
            Tick = long.MaxValue, NativeGeneration = ulong.MaxValue };
        RoundTrip(context, Shared.ObservationContext.Parser, "csharp-context", output);
        Require(context.Identity.HasMapId && context.HasTick && context.HasNativeGeneration,
            "Context preserves map zero and full-width generation/tick presence");
        var authority = new Control.Status { Context = context,
            Inactive = new Control.InactiveAuthority { Reason = Control.RevocationReason.Manual } };
        RoundTrip(authority, Control.Status.Parser, "csharp-authority-inactive", output);
        authority.Active = new Control.ActiveAuthority { Mode = Control.Mode.Auto };
        Require(authority.Inactive == null && authority.StateCase == Control.Status.StateOneofCase.Active,
            "Authority state cannot retain active and inactive variants together");
        RoundTrip(authority, Control.Status.Parser, "csharp-authority-active", output);
        var setMode = new Control.ControlRequest { SetMode = new Control.SetMode {
            Identity = context.Identity, ExpectedGeneration = 1, Mode = authority.Active.Mode } };
        RoundTrip(setMode, Control.ControlRequest.Parser, "csharp-authority-set-mode", output);
        var descriptors = CanonicalMessages();
        FullPackage(descriptors, output);
        FamilyExamples(descriptors, output);
        if (args.Length == 2)
        {
            var incoming = args[1];
            var goRequest = PlacementRequest.Parser.ParseJson(File.ReadAllText(Path.Combine(incoming, "go-request.json")));
            Require(goRequest.Equals(PlacementRequest.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(incoming, "go-request.bin")))),
                "Official Go request ProtoJSON and binary agree in C#");
            var goReply = PlacementReply.Parser.ParseJson(File.ReadAllText(Path.Combine(incoming, "go-reply.json")));
            Require(goReply.Equals(PlacementReply.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(incoming, "go-reply.bin")))),
                "Official Go reply ProtoJSON and binary agree in C#");
            RoundTrip(goRequest, PlacementRequest.Parser, "go-echo-request", output);
            RoundTrip(goReply, PlacementReply.Parser, "go-echo-reply", output);
            var goU64 = UInt64Value.Parser.ParseJson(File.ReadAllText(Path.Combine(incoming, "go-u64.json")));
            Require(goU64.Equals(UInt64Value.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(incoming, "go-u64.bin"))))
                && goU64.Value == ulong.MaxValue, "Official Go uint64 preserves all 64 bits in C#");
            RoundTrip(goU64, UInt64Value.Parser, "go-echo-u64", output);
            var goContext = Shared.ObservationContext.Parser.ParseJson(File.ReadAllText(Path.Combine(incoming, "go-context.json")));
            Require(goContext.Equals(Shared.ObservationContext.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(incoming, "go-context.bin"))))
                && goContext.NativeGeneration == ulong.MaxValue && goContext.Tick == long.MaxValue
                && goContext.Identity.HasMapId && goContext.Identity.MapId == 0,
                "Official Go native context preserves identity presence and numeric extremes in C#");
            RoundTrip(goContext, Shared.ObservationContext.Parser, "go-echo-context", output);
            IncomingManifest(descriptors, incoming, output);
        }
        File.WriteAllLines(Path.Combine(output, "manifest.tsv"), manifest, new UTF8Encoding(false));
        Console.WriteLine("Official Protobuf net472 proof passed: " + checks + " checks; artifacts " + output);
    }
}
