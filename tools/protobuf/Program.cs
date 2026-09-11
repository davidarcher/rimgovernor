using System;
using System.IO;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using RimGovernor.Protocol.Placement;

internal static class Program
{
    private static int checks;
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

    public static void Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Supply a fresh proof artifact directory");
        var output = args[0];
        if (Directory.Exists(output)) throw new ArgumentException("Proof artifact directory must be fresh");
        Directory.CreateDirectory(output);
        var candidate = new PlacementCandidate { DefName = "Wall", X = 0, Z = int.MaxValue,
            Rotation = Rotation.North, Stuff = "" };
        var request = new PlacementRequest();
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
            BuildableByPlayer = true, Materials = new PlacementMaterials { Unreadable = true } };
        evaluated.Materials.Rows.Add(missing);
        evaluated.Materials.Rows.Add(empty);
        evaluated.CostList.Add(new PlacementCost { DefName = "WoodLog", Count = 5 });
        var rotation = new PlacementRotation { Rotation = Rotation.North, Accepted = false, Reason = "Blocked" };
        rotation.OccupiedCells.Add(new PlacementCell { X = int.MinValue, Z = 0 });
        rotation.BlockingThings.Add(new PlacementBlocker { Category = "Building", IsBlueprint = false,
            IsFrame = false, WouldBeWiped = false, FrameWouldBeCancelled = false });
        evaluated.Rotations.Add(rotation);
        var batch = new PlacementBatch { Tick = 0, MapId = 0 };
        batch.Results.Add(new CandidateReply { Evaluated = evaluated });
        batch.Results.Add(new CandidateReply { Failure = new PlacementFailure { Error = "Unknown definition" } });
        var reply = new PlacementReply { Batch = batch };
        RoundTrip(reply, PlacementReply.Parser, "csharp-reply", output);
        var decoded = PlacementReply.Parser.ParseJson(JsonFormatter.Default.Format(reply));
        Require(decoded.OutcomeCase == PlacementReply.OutcomeOneofCase.Batch, "Batch oneof arm retained");
        Require(decoded.Batch.Results[0].OutcomeCase == CandidateReply.OutcomeOneofCase.Evaluated,
            "Evaluated candidate retained");
        Require(decoded.Batch.Results[0].Evaluated.HasCanPlace && !decoded.Batch.Results[0].Evaluated.CanPlace,
            "Present false is a known refusal");
        Require(!decoded.Batch.Results[0].Evaluated.Materials.Rows[0].HasAvailable
            && decoded.Batch.Results[0].Evaluated.Materials.Rows[1].HasAvailable, "Stock presence round trip");
        Require(decoded.Batch.Results[1].OutcomeCase == CandidateReply.OutcomeOneofCase.Failure,
            "Candidate semantic failure remains separate from batch failure");
        var failure = new PlacementReply { Failure = new PlacementFailure { Error = "Invalid request" } };
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
        if (args.Length == 2)
        {
            var incoming = args[1];
            var goRequest = PlacementRequest.Parser.ParseJson(File.ReadAllText(Path.Combine(incoming, "go-request.json")));
            Require(goRequest.Equals(PlacementRequest.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(incoming, "go-request.bin")))),
                "Official Go request ProtoJSON and binary agree in C#");
            var goReply = PlacementReply.Parser.ParseJson(File.ReadAllText(Path.Combine(incoming, "go-reply.json")));
            Require(goReply.Equals(PlacementReply.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(incoming, "go-reply.bin")))),
                "Official Go reply ProtoJSON and binary agree in C#");
            RoundTrip(goRequest, PlacementRequest.Parser, "csharp-request", output);
            RoundTrip(goReply, PlacementReply.Parser, "csharp-reply", output);
            var goU64 = UInt64Value.Parser.ParseJson(File.ReadAllText(Path.Combine(incoming, "go-u64.json")));
            Require(goU64.Equals(UInt64Value.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(incoming, "go-u64.bin"))))
                && goU64.Value == ulong.MaxValue, "Official Go uint64 preserves all 64 bits in C#");
            RoundTrip(goU64, UInt64Value.Parser, "csharp-u64", output);
        }
        Console.WriteLine("Official Protobuf net472 proof passed: " + checks + " checks; artifacts " + output);
    }
}
