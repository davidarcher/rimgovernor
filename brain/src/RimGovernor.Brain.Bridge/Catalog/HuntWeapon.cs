using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go hunt_weapon.go: what the def rows say of a hunter's primary weapon (the rule the native hunt census used to
// state over ThingDef.verbs).

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// What the def rows say of a hunter's primary weapon: whether the def is a ranged or melee weapon, and each
    /// verb's range, warmup, ai-weapon flag, default projectile class, explosion radius and damage def and worker.
    /// Null for "" (no weapon); a def that is no weapon is the unarmed bare value; a def without a row, a projectile
    /// row or a damage def row is an error.
    /// </summary>
    public P.HuntWeapon? HuntWeapon(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        var row = ThingRow(name);
        var weapon = new P.HuntWeapon { DefName = name };

        // ThingDef.IsWeapon: an item with verbs or tools that is no apparel.
        if (row.category != ThingCategory.Item || row.Verbs.Seq().Count() + row.tools.Seq().Count() == 0 || row.apparel is not null)
        {
            return weapon;
        }

        var verbs = new List<P.HuntVerb>();
        var ranged = false;
        foreach (var props in row.Verbs.Seq())
        {
            var melee = props.verbClass is { } kind && typeof(Verb_MeleeAttack).IsAssignableFrom(kind);
            ranged |= !melee;
            var verb = new P.HuntVerb
            {
                Melee = melee,
                AIWeapon = props.ai_IsWeapon,
                Range = props.range,
                Warmup = props.warmupTime,
            };
            if (props.defaultProjectile is { } projectile)
            {
                var projectileRow = ThingRow(projectile.defName);
                verb = verb with
                {
                    Projectile = projectileRow.thingClass == typeof(Bullet) ? P.HuntProjectile.Bullet : P.HuntProjectile.Other,
                };
                if (projectileRow.projectile is { } properties)
                {
                    verb = verb with { ExplosionRadius = properties.explosionRadius };
                    if (properties.damageDef is { } damage)
                    {
                        var damageRow = DefRow<DamageDef>(damage.defName)
                            ?? throw new CatalogException($"catalog has no damage def row for {damage.defName}, the damage of {name}");
                        verb = verb with { DamageDef = damage.defName, DamageWorker = damageRow.workerClass?.FullName ?? "" };
                    }
                }
            }

            verbs.Add(verb);
        }

        return weapon with { Melee = row.tools.Seq().Any(), Ranged = ranged, Verbs = verbs };
    }
}
