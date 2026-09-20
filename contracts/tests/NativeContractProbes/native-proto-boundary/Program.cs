using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Threading;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Common = RimGovernor.Protocol.Common;

internal static class NativeProtoBoundaryProbe {
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static bool Parse(object request, out Common.Identity value, out Common.Failure failure) {
        return ProtoBoundary.TryParse(null, "test", request, Common.Identity.Parser, out value, out failure);
    }
    static void Case(string name, string json, bool accepted) {
        BridgeCommon.Arguments = new Dictionary<string,object> { ["request"] = json };
        Common.Identity value; Common.Failure failure;
        Check(Parse(json, out value, out failure) == accepted, name);
        Check(accepted ? failure == null : failure.Code == Common.FailureCode.InvalidRequest && value == null, name+" outcome");
    }
    internal static void Invoke(string[] args) {
        CompactObservations();
        PackedPlanningCells();
        Case("empty identity syntax", "{}", true);
        Case("unknown field", "{\"unknown\":1}", false);
        Case("trailing junk", "{}x", false);
        Case("trailing object", "{}{}", false);
        Case("trailing comma", "{\"mapId\":1,}", false);
        Case("invalid escape", "{\"colonyId\":\"\\q\"}", false);
        Case("unterminated", "{\"colonyId\":\"", false);
        Case("array root", "[]", false);
        Case("null root", "null", false);
        Case("scalar root", "1", false);
        Case("map overflow", "{\"mapId\":2147483648}", false);
        Case("map fraction", "{\"mapId\":1.25}", false);
        Case("integer string", "{\"mapId\":\"12\"}", true);
        Case("negative map syntax", "{\"mapId\":-1}", true);
        Case("null fact syntax", "{\"mapId\":null}", true);
        Case("actual lone high surrogate", "{\"colonyId\":\""+'\ud800'+"\"}", false);
        Case("actual lone low surrogate", "{\"colonyId\":\""+'\udc00'+"\"}", false);
        Case("escaped lone high surrogate", "{\"colonyId\":\"\\ud800\"}", false);
        Case("escaped lone low surrogate", "{\"colonyId\":\"\\udc00\"}", false);
        Case("escaped reversed pair", "{\"colonyId\":\"\\udc00\\ud800\"}", false);
        Case("valid pair", "{\"colonyId\":\"\\ud83d\\ude00\"}", true);
        Case("byte cap inclusive", "{}"+new string(' ',ProtoBoundary.MaximumEnvelopeBytes-2), true);
        Case("UTF8 byte cap differs from chars", "{\"colonyId\":\""+new string('\u00e9',ProtoBoundary.MaximumEnvelopeBytes/2)+"\"}", false);
        Case("byte cap refusal", "{}"+new string(' ',ProtoBoundary.MaximumEnvelopeBytes-1), false);
        Common.Identity parsed; Common.Failure refusal;
        BridgeCommon.Arguments=null;
        Check(!Parse("{}",out parsed,out refusal) && refusal.Code==Common.FailureCode.Unavailable,"missing journal");
        BridgeCommon.Arguments=new Dictionary<string,object>{["request"]="{}",["other"]=true};
        Check(!Parse("{}",out parsed,out refusal),"unknown outer argument");
        BridgeCommon.Arguments=new Dictionary<string,object>{["request"]=new JValue("{}"),["_rimBridgeTimeoutMs"]=10};
        Check(Parse("{}",out parsed,out refusal),"raw JValue exact string");
        Check(!Parse(" {}",out parsed,out refusal),"raw/bound argument mismatch");
        BridgeCommon.Arguments=new Dictionary<string,object>{["request"]=new JObject()};
        Check(!Parse(new JObject(),out parsed,out refusal),"raw object not coerced");
        Check(!ProtoBoundary.IsIdentifier("\ud800"),"identifier surrogate");
        Check(!ProtoBoundary.IsIdentifier("a\0b"),"identifier NUL");
        Check(ProtoBoundary.IsIdentifier(new string('\u00e9',128)),"identifier UTF8 bound");
        Check(!ProtoBoundary.IsIdentifier(new string('\u00e9',129)),"identifier UTF8 over");
        var reply=new Common.Identity{ColonyId="colon\u00ff",LoadToken="load",MapId=0};
        var wire=ProtoBoundary.Encode(reply);
        Check(wire.Count==1 && wire["payload"] is string,"single string payload");
        Check(reply.Equals(Common.Identity.Parser.ParseJson((string)wire["payload"])),"official payload round trip");
        var sdkJson=JObject.Parse(JsonConvert.SerializeObject(wire));
        Check(sdkJson["payload"].Type==JTokenType.String && sdkJson.Count==1,"outer Newtonsoft dictionary encoding");
        var overflowRefused=false;
        try { ProtoBoundary.Encode(new Common.Identity { ColonyId=new string('a',ProtoBoundary.MaximumEnvelopeBytes) }); }
        catch(InvalidOperationException) { overflowRefused=true; }
        Check(overflowRefused,"oversize outgoing payload fails before return");
        Common.ObservationContext context; Common.Unavailable unavailable;
        Check(!ProtoBoundary.TryReadContext(null,out context,out unavailable) && unavailable.Reason==Common.UnavailableReason.NotLoaded,"unloaded read does not create authority");
        var game = new Verse.Game { Identity = new ColonyIdentity { ColonyId="colony",LoadToken="load" } };
        var map = new Verse.Map { uniqueID=1 };
        Verse.Current.Game=game; Verse.Find.CurrentMap=map; Verse.Find.TickManager=new Verse.TickManager { TicksGame=12 };
        NativeControlAuthority authority;
        Check(!NativeControlAuthority.TryGetForGame(game,out authority),"new game no authority");
        Check(ProtoBoundary.TryReadContext(map,out context,out unavailable) && !context.HasNativeGeneration,"read with absent authority leaves generation absent");
        Check(!NativeControlAuthority.TryGetForGame(game,out authority),"read does not allocate authority");
        var owner=NativeControlAuthority.ForGame(game);
        Check(NativeControlAuthority.TryGetForGame(game,out authority) && ReferenceEquals(owner,authority),"existing authority identity preserved");
        Check(ProtoBoundary.TryReadContext(map,out context,out unavailable) && context.NativeGeneration==owner.Status().Generation,"read reports existing owner generation");
        Check(!NativeControlAuthority.TryGetForGame(new Verse.Game(),out authority),"another game isolated");
        Check(!NativeControlAuthority.TryGetForGame(null,out authority),"null game unavailable");
        if(args.Length!=1) throw new ArgumentException("Supply installed RimBridgeServer.dll for real SDK binder checks");
        CheckSdkBinder(args[0]);
        Console.WriteLine("Boundary probe passed: "+checks+" checks; actual SDK binder plus journal/game seams; no journal integration or gameplay acceptance.");
    }
    static void PackedPlanningCells() {
        var snapshot = new RimGovernor.Protocol.Observations.CellsSnapshot {
            Region = new RimGovernor.Protocol.Observations.Rectangle {
                Minimum = new Common.Cell { X = 7, Z = 8 }, Maximum = new Common.Cell { X = 9, Z = 8 } }
        };
        snapshot.Cells.Add(new RimGovernor.Protocol.Observations.CellState {
            Cell = new Common.Cell { X = 7, Z = 8 }, Walkable = true, Passable = true,
            Occupied = true, Doorway = true, SupportsLight = true, StorageEmpty = true,
            Indoors = true, Polluted = true, Roof = "RoofConstructed", ZoneId = "7", RoomId = "9",
            Glow = .123456789, Fertility = 1.23456789
        });
        snapshot.Cells.Add(new RimGovernor.Protocol.Observations.CellState { Cell = new Common.Cell { X = 8, Z = 8 }, Fogged = true });
        CompactCellEncoding.Encode(snapshot);
        Check(snapshot.Cells.Count == 0 && snapshot.Compact.Rows[0].Equals(ByteString.CopyFrom(new byte[] { 0xfc, 0x3f, 0, 1, 2, 0, 2, 0, 1, 0 })), "packed flags match Go decoder golden including fog and unchanged");
        Check(snapshot.Compact.Fertility[0] == 1.23456789 && snapshot.Compact.Glow[0] == .123456789 && snapshot.Compact.Strings[2] == "9", "packed metadata lossless");
        snapshot = new RimGovernor.Protocol.Observations.CellsSnapshot {
            Region = new RimGovernor.Protocol.Observations.Rectangle {
                Minimum = new Common.Cell { X = 0, Z = 0 }, Maximum = new Common.Cell { X = 249, Z = 249 } }
        };
        for (int z = 0; z < 250; z++) for (int x = 0; x < 250; x++)
            snapshot.Cells.Add(new RimGovernor.Protocol.Observations.CellState {
                Cell = new Common.Cell { X = x, Z = z }, Walkable = true, Passable = true,
                SupportsLight = true, StorageEmpty = true, Indoors = false, Polluted = false,
                Occupied = false, Doorway = false, Fertility = 1, Glow = 1, RoomId = "1"
            });
        CompactCellEncoding.Encode(snapshot);
        var json = (string)ProtoBoundary.Encode(snapshot, compact: true)["payload"];
        Check(Encoding.UTF8.GetByteCount(json) < 768 * 1024, "250x250 coverage retains envelope headroom");
        Console.WriteLine("Compact 250x250 planning bytes/cell: " + Encoding.UTF8.GetByteCount(json) / 62500.0);
    }
    static void RawTransport(object request=null) { }
    static void CompactObservations() {
        var cells = new RimGovernor.Protocol.Observations.CellsSnapshot();
        for (int i = 0; i < 2025; i++) {
            var cell = new RimGovernor.Protocol.Observations.CellState {
                Cell = new Common.Cell { X = i % 45, Z = i / 45 },
                Terrain = "2026-09-12T00:00:00Z", Roof = "spaces and \"quotes\" \\ \u00e9",
                Fertility = .12345678901234567, TemperatureC = i % 2 == 0 ? double.Epsilon : double.MaxValue,
                Fogged = false, Walkable = true, Occupied = false, Indoors = true
            };
            cells.Cells.Add(cell);
        }
        var compact = (string)ProtoBoundary.Encode(cells, compact: true)["payload"];
        Check(RimGovernor.Protocol.Observations.CellsSnapshot.Parser.ParseJson(compact).Equals(cells),
            "compact complete grid preserves strings, false presence, floating values and all cells");
        Check(Encoding.UTF8.GetByteCount(compact) < Encoding.UTF8.GetByteCount(ProtoBoundary.Format(cells)),
            "compact grid reduces envelope bytes");
        bool refused = false;
        try { ProtoBoundary.Encode(new Common.Identity { ColonyId = new string('x', ProtoBoundary.MaximumEnvelopeBytes) }, compact: true); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "compact reply retains byte limit");
        var capture = Environment.GetEnvironmentVariable("RIMGOVERNOR_NATIVE_COLONY_CAPTURE");
        if (!string.IsNullOrEmpty(capture)) {
            var original = RimGovernor.Protocol.Observations.ColonyFactsReply.Parser.ParseJson(System.IO.File.ReadAllText(capture));
            var encoded = (string)ProtoBoundary.Encode(original, compact: true)["payload"];
            Check(RimGovernor.Protocol.Observations.ColonyFactsReply.Parser.ParseJson(encoded).Equals(original), "native capture retains complete ProtoJSON values");
            Console.WriteLine("Native colony payload bytes: " + Encoding.UTF8.GetByteCount(ProtoBoundary.Format(original)) + " -> " + Encoding.UTF8.GetByteCount(encoded));
        }
    }
    static void CheckSdkBinder(string serverPath) {
        var provider=Assembly.LoadFrom(serverPath).GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider",true);
        var binder=provider.GetMethod("BindArguments",BindingFlags.NonPublic|BindingFlags.Static);
        var method=typeof(NativeProtoBoundaryProbe).GetMethod("RawTransport",BindingFlags.NonPublic|BindingFlags.Static);
        foreach(var raw in new object[]{"{}",new JObject(),new JArray(),null,17,true}) {
            var args=new Dictionary<string,object>{["request"]=raw};
            var bound=(object[])binder.Invoke(null,new object[]{method,args,null,CancellationToken.None});
            Check(ReferenceEquals(bound[0],raw),"actual SDK object parameter preserves raw value");
            BridgeCommon.Arguments=args;
            Common.Identity parsed; Common.Failure failure;
            Check(Parse(bound[0],out parsed,out failure)==(raw is string),"bound value string-only validation");
        }
        var absent=(object[])binder.Invoke(null,new object[]{method,new Dictionary<string,object>(),null,CancellationToken.None});
        Check(absent[0]==null,"actual SDK missing object argument remains null");
    }

}
