namespace RimGovernor.Brain.Simulation.Combat;

// Thing ids and the tick phase they set (A9). The game ticks an off-camera Thing when (TicksGame +
// Gen.HashOffset(thingID)) % 15 == 0, so a projectile's landing tick follows from the id it was given. The sim
// hands out ids in the order the game does, from a lab-start id, and models the one thing that consumes ids
// between shots that it can: the blood filth a wounded pawn drops.
internal static class ThingIds
{
    /// <summary>
    /// The lowest id the first projectile of a lab-ranged run took across the 20 recorded seeds (7308); the
    /// resumed checkpoint's own spawns decide it, 0 to 3 higher.
    /// </summary>
    public const int FirstProjectileId = 7308;

    /// <summary>
    /// The ids a damaging hit costs on average: the recorded gaps between consecutive projectile ids total 356
    /// extra ids over 410 damaging impacts. PostApplyDamage drops blood filth with chance 0.5; the rest are
    /// bleeding drops (Pawn_HealthTracker.HealthTick) after the hit, lumped into the hit, and filth that
    /// stacks on an existing one takes none.
    /// </summary>
    public const double FilthIdsPerDamagingHit = 356.0 / 410.0;

    /// <summary>The thingIDNumber a pawn id carries after its def name ("Thing_Human7218" is 7218); an id without digits is 0.</summary>
    public static int ThingNumber(string pawnId)
    {
        var n = pawnId.Length;
        while (n > 0 && pawnId[n - 1] is >= '0' and <= '9')
        {
            n--;
        }
        long id = 0;
        for (var i = n; i < pawnId.Length; i++)
        {
            id = (id * 10) + (pawnId[i] - '0');
            if (id > int.MaxValue)
            {
                return 0;
            }
        }
        return (int)id;
    }

    /// <summary>Draws the id of a run's first projectile: 7308 to 7311 with the frequencies 7, 7, 4 and 2 in 20 the recorded seeds showed.</summary>
    public static int FirstThingId(GoRandom r)
    {
        var n = r.NextInt(20);
        if (n < 7)
        {
            return FirstProjectileId;
        }
        if (n < 14)
        {
            return FirstProjectileId + 1;
        }
        if (n < 18)
        {
            return FirstProjectileId + 2;
        }
        return FirstProjectileId + 3;
    }

    /// <summary>Ports Verse.Gen.HashOffset: Gen.HashCombineInt(id, 169495093) (int arithmetic wraps, the shift of a negative id is arithmetic).</summary>
    public static int HashOffset(int id)
    {
        long v = 169495093L + 2654435769L + unchecked(id << 6) + (id >> 2);
        return unchecked((int)(id ^ v));
    }

    /// <summary>The residue mod the off-camera interval a thing with this id is ticked on: the ticks t with (t + HashOffset(id)) % 15 == 0.</summary>
    public static int IdPhase(int id)
    {
        var r = HashOffset(id) % ShotMath.OffCameraInterval;
        return (ShotMath.OffCameraInterval - r) % ShotMath.OffCameraInterval;
    }
}
