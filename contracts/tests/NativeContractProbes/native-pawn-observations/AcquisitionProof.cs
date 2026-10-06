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
        // The raw verb facts Go's ordinary-weapon rule reads (policy.OrdinaryHuntVerbs): bullet-class
        // projectile, no blast radius, no flame damage worker. An arrow is a Bullet-class projectile
        // with an arrow damage def, so it passes the same projectile-class test a bullet does.
        Func<object,string,object?> fact=(value,name)=>value.GetType().GetProperty(name)!.GetValue(value);
        Func<object,object> facts=v=>bridge.GetType("HomeBridge.BridgeTools.NativeHuntAcquisition",true)!.GetMethod("VerbFacts",flags)!.Invoke(null,new object[]{v})!;
        var bullet=verb("RimWorld.Bullet",0);var explosive=verb("Verse.Projectile_Explosive",2);
        check(fact(facts(bullet),"ProjectileKind")!.ToString()=="Bullet","bullet projectile classified");
        check(fact(facts(explosive),"ProjectileKind")!.ToString()=="Other","explosive projectile classified other");
        check(Convert.ToDouble(fact(facts(explosive),"ExplosionRadius"))==2,"blast radius reported");
        check((bool)fact(facts(bullet),"AiWeapon")!,"ai weapon verb reported");
        var arrowDamage = FormatterServices.GetUninitializedObject(damageType);
        damageType.GetField("defName")!.SetValue(arrowDamage, "Arrow");
        var arrow = verb("RimWorld.Bullet", 0);
        var arrowProjectile = thingDef.GetField("projectile")!.GetValue(verbType.GetField("defaultProjectile")!.GetValue(arrow)!)!;
        projectileType.GetField("damageDef")!.SetValue(arrowProjectile, arrowDamage);
        check(fact(facts(arrow),"ProjectileKind")!.ToString()=="Arrow","arrow projectile classified");
        var fire = verb("RimWorld.Bullet", 0);
        var fireProjectile = thingDef.GetField("projectile")!.GetValue(verbType.GetField("defaultProjectile")!.GetValue(fire)!)!;
        projectileType.GetField("damageDef")!.SetValue(fireProjectile, flameDamage);
        check((string?)fact(facts(fire),"DamageWorker")=="Verse.DamageWorker_Flame","incendiary damage worker reported");
        var noProjectile=Activator.CreateInstance(verbType)!;
        verbType.GetField("verbClass")!.SetValue(noProjectile,native.GetType("Verse.Verb_Shoot",true));
        check(fact(facts(noProjectile),"ProjectileKind")!.ToString()=="Unspecified","missing projectile unclassified");
    }
}
