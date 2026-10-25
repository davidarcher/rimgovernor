#nullable enable
using System;
using System.Reflection;
internal static class ZoneProof {
 internal static void Run(Assembly bridge,Action<bool,string> check){
 const BindingFlags flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 var zoneIntent=typeof(RimGovernor.Contracts.Wire.Common.Ref).Assembly.GetType("RimGovernor.Contracts.Wire.Operations.ZoneIntent",true)!;
 Func<string,object> parse=json=>typeof(RimGovernor.Contracts.Wire.WireJson).GetMethod("Deserialize",new[]{typeof(string)})!.MakeGenericMethod(zoneIntent).Invoke(null,new object[]{json})!;
 var type=bridge.GetType("HomeBridge.BridgeTools.NativeZoneCreation",true)!;
 const string json="{\"delete\":false,\"requireCoveredEmpty\":false,\"kind\":\"ZONE_TYPE_GROWING\",\"label\":\"Crops\",\"addCells\":{\"selection\":{\"type\":\"explicitCells\",\"value\":{\"cells\":[{\"x\":0,\"z\":0},{\"x\":1,\"z\":0}]}}},\"growing\":{\"plantDef\":\"Plant_Rice\"}}";
 bool valid(string s) { try { return (bool)type.GetMethod("Valid",flags)!.Invoke(null,new[]{parse(s)})!; } catch(TargetInvocationException ex) when(ex.InnerException is System.Text.Json.JsonException) { return false; } }
 check(valid(json),"native growing configuration valid");
 foreach(var bad in new[]{"{}",json.Replace("Plant_Rice",""),json.Replace("\"x\":1","\"x\":0")})check(!valid(bad),"native zone rejects malformed configuration");
 }
}
