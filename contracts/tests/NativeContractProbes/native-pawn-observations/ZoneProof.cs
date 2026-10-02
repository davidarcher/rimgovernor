#nullable enable
using System;
using System.Reflection;
internal static class ZoneProof {
 internal static void Run(Assembly bridge,Action<bool,string> check){
 const BindingFlags flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 var parser=bridge.GetType("RimGovernor.Protocol.Operations.CreateZone",true)!.GetProperty("Parser")!.GetValue(null)!;
 Func<string,object> parse=json=>parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;
 var type=bridge.GetType("HomeBridge.BridgeTools.NativeZoneCreation",true)!;
 const string json="{\"type\":\"ZONE_TYPE_GROWING\",\"label\":\"Crops\",\"cells\":{\"explicitCells\":{\"cells\":[{\"x\":0,\"z\":0},{\"x\":1,\"z\":0}]}},\"growing\":{\"plantDef\":\"Plant_Rice\",\"allowSow\":true,\"allowCut\":true}}";
 Func<string,bool> valid=s=>(bool)type.GetMethod("Valid",flags)!.Invoke(null,new[]{parse(s)})!;
 check(valid(json),"native growing configuration valid");
 foreach(var bad in new[]{"{}",json.Replace("Plant_Rice",""),json.Replace("\"allowSow\":true","\"allowSow\":false"),json.Replace("\"x\":1","\"x\":0")})check(!valid(bad),"native zone rejects malformed configuration");
 }
}
