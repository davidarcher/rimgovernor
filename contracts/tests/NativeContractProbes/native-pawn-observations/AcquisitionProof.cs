#nullable enable
using System;
using System.Reflection;
using System.Runtime.Serialization;

internal static class AcquisitionProof
{
    internal static void Run(Assembly bridge, Action<bool,string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Func<string,string,object> parse = (name,json) => {
            var parser = bridge.GetType("RimGovernor.Protocol."+name,true)!.GetProperty("Parser")!.GetValue(null)!;
            return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;
        };
        var adapter=bridge.GetType("HomeBridge.BridgeTools.NativePlantAcquisition",true)!;
        Func<string,bool> valid=json=>(bool)adapter.GetMethod("Valid",flags)!.Invoke(null,new[]{parse("Operations.AcquireResource",json)})!;
        const string source="\"source\":{\"entityId\":\"Plant1\",\"expectedSnapshotToken\":\"token\"}";
        check(valid("{"+source+",\"resourceDefName\":\"WoodLog\",\"cell\":{\"x\":0,\"z\":0}}"),"exact acquisition accepts explicit origin");
        foreach(var json in new[]{"{}","{"+source+",\"resourceDefName\":\"WoodLog\"}","{"+source+",\"resourceDefName\":\"WoodLog\",\"cell\":{\"x\":0}}","{"+source+",\"resourceDefName\":\"WoodLog\",\"cell\":{\"x\":-1,\"z\":0}}"}) check(!valid(json),"acquisition refuses incomplete source or cell");
        var identity=parse("Common.Identity","{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}");
        object[] inputs={identity,"Plant1","WoodLog",0,0,1f,10,false};
        Func<object[],string> token=values=>(string)adapter.GetMethod("Token",flags)!.Invoke(null,values)!;
        var original=token(inputs); check(original==token(inputs),"stable acquisition token");
        object[] replacements={parse("Common.Identity","{\"colonyId\":\"colony\",\"loadToken\":\"new-load\",\"mapId\":0}"),"Plant2","RawBerries",1,1,0.9f,11,true};
        for(var i=0;i<inputs.Length;i++) {var changed=(object[])inputs.Clone();changed[i]=replacements[i];check(token(changed)!=original,"acquisition token binds field "+i);}
        var native=Assembly.Load("Assembly-CSharp");
        var verbType=native.GetType("Verse.VerbProperties",true)!;
        var thingDef=native.GetType("Verse.ThingDef",true)!;
        var projectileType=native.GetType("Verse.ProjectileProperties",true)!;
        var damageType = native.GetType("Verse.DamageDef", true)!;
        var bulletDamage = FormatterServices.GetUninitializedObject(damageType);
        var flameDamage = FormatterServices.GetUninitializedObject(damageType);
        damageType.GetField("workerClass")!.SetValue(flameDamage, native.GetType("Verse.DamageWorker_Flame", true));
        Func<string,float,object> verb=(kind,radius)=> {
            var definition=FormatterServices.GetUninitializedObject(thingDef)!;
            thingDef.GetField("thingClass")!.SetValue(definition,native.GetType(kind,true));
            var projectile=Activator.CreateInstance(projectileType)!;
            projectileType.GetField("explosionRadius")!.SetValue(projectile,radius);
            projectileType.GetField("damageDef")!.SetValue(projectile,bulletDamage);
            thingDef.GetField("projectile")!.SetValue(definition,projectile);
            var value=Activator.CreateInstance(verbType)!;
            verbType.GetField("verbClass")!.SetValue(value,native.GetType("Verse.Verb_Shoot",true));
            verbType.GetField("ai_IsWeapon")!.SetValue(value,true);
            verbType.GetField("defaultProjectile")!.SetValue(value,definition);
            return value;
        };
        Func<object[],bool> ordinary=values=> {
            var list=Array.CreateInstance(verbType,values.Length);for(var i=0;i<values.Length;i++) list.SetValue(values[i],i);
            return (bool)bridge.GetType("HomeBridge.BridgeTools.NativeHuntAcquisition",true)!.GetMethod("OrdinaryVerbs",flags)!.Invoke(null,new object[]{list})!;
        };
        var bullet=verb("RimWorld.Bullet",0);var explosive=verb("Verse.Projectile_Explosive",2);
        check(ordinary(new[]{bullet}),"ordinary hunting projectile accepted");
        check(!ordinary(new[]{explosive}),"explosive hunting projectile excluded");
        check(!ordinary(new[]{bullet,explosive}),"secondary safe verb cannot admit explosive weapon");
        check(!ordinary(new[]{verb("RimWorld.Bullet",1)}),"bullet with blast radius excluded");
        check(!ordinary(Array.Empty<object>()),"missing hunting projectile unavailable");
        var fire = verb("RimWorld.Bullet", 0);
        var fireDef = verbType.GetField("defaultProjectile")!.GetValue(fire)!;
        var fireProjectile = thingDef.GetField("projectile")!.GetValue(fireDef)!;
        projectileType.GetField("damageDef")!.SetValue(fireProjectile, flameDamage);
        check(!ordinary(new[]{fire}), "incendiary bullet excluded");
        check(!ordinary(new[]{bullet,fire}), "secondary incendiary verb excludes weapon");

        var record=bridge.GetType("HomeBridge.BridgeTools.NativeAcquisitionRecord",true)!;
        var attempt=parse("Common.AttemptKey","{\"controllerSessionId\":\"session\",\"actionId\":\"action\",\"attemptId\":1}");
        var context=parse("Common.ObservationContext","{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":1}");
        foreach(var test in new[]{
            new[]{"{\"designated\":true,\"laborFinished\":false,\"producedUnits\":0,\"outputComplete\":true,\"outputObserved\":false}","Pending"},
            new[]{"{\"designated\":false,\"laborFinished\":true,\"producedUnits\":10,\"outputComplete\":true,\"outputObserved\":true}","Completed"},
            new[]{"{\"laborFinished\":true,\"producedUnits\":0,\"outputComplete\":true}","Unsuccessful"},
            new[]{"{\"laborFinished\":true,\"producedUnits\":10,\"outputComplete\":true,\"outputObserved\":false}","Unknown"},
            new[]{"{\"laborFinished\":true,\"producedUnits\":0,\"outputComplete\":false}","Unknown"},
            new[]{"{\"laborFinished\":false,\"designated\":false,\"producedUnits\":0,\"outputComplete\":true}","Unknown"}
        }) {
            var effect=parse("Receipts.AcquisitionEffect",test[0]);
            var result=record.GetMethod("Progress",flags)!.Invoke(null,new[]{attempt,context,effect})!;
            check(result.GetType().GetProperty("EffectCase")!.GetValue(result)!.ToString()==test[1],"actual acquisition evidence gives "+test[1]);
        }
    }
}
