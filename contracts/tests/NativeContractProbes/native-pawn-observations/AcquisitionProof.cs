#nullable enable
using System;
using System.Reflection;
using System.Runtime.Serialization;

internal static class AcquisitionProof
{
    internal static void Run(Assembly bridge, Action<bool,string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
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

    }
}
