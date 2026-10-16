namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>The reform the colony's ideoligion owes, or unknown while any input is unread or the pawns disagree on one faction.</summary>
    internal static Fact<DesignChoice> ReformChoice(RoundsReading read, GenerationSnapshot snapshot)
    {
        if (!read.Projection.Facts.Ideology.TryGetValue(out var ideo) || read.Catalog is not { } catalog || read.Frame.Pawns is null)
        {
            return Unknown<DesignChoice>();
        }

        var faction = "";
        foreach (var pawn in read.Frame.Pawns.Pawns)
        {
            if (!pawn.FreeColonist.GetValueOrDefault() || pawn.Dead.GetValueOrDefault())
            {
                continue;
            }

            var name = pawn.Standing?.FactionDefName ?? "";
            if (name.Length == 0 || (faction.Length != 0 && faction != name))
            {
                return Unknown<DesignChoice>();
            }

            faction = name;
        }

        DesignOptions options;
        try
        {
            options = catalog.IdeoligionOptions(faction);
        }
        catch (DomainException)
        {
            return Unknown<DesignChoice>();
        }

        options = options with { Needs = PolicyFunctions.IdeoligionNeeds(ideo.Facts, read.Projection.WorkPawns, read.Projection.Facts.MoodPawns) };
        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = catalog.DefRow<RimWorld.PreceptDef>(held.Def);
            if (def is null)
            {
                return Unknown<DesignChoice>();
            }

            if (def.preceptClass == typeof(RimWorld.Precept))
            {
                precepts.Add(held.Def);
            }
        }

        var current = new IdeoligionDesign([.. ideo.Facts.Memes], [.. precepts], true);
        var calm = Unknown<bool>();
        var tick = read.Projection.Identity.Tick;
        try
        {
            var emergency = EmergencySnapshot.New(snapshot, tick, read.Emergency);
            var (hostiles, patients) = PolicyFunctions.EmergencyNeeds(emergency, snapshot, tick);
            calm = PolicyFunctions.RitualCalm(hostiles, patients);
        }
        catch (DomainException)
        {
            // No emergency snapshot leaves calm unknown.
        }

        return PolicyFunctions.ChooseIdeoligionReform(options, current, ideo.Facts.Development, calm);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Whether the colony's ideoligion now matches the intended design: unknown when the ideo or its development is unread, false while the reform count has not advanced.</summary>
    internal static Fact<bool> ObservedReform(Ideoligion ideo, IdeoligionDesign actual, IdeoligionReform intent)
    {
        if (!ideo.Facts.Development.TryGetValue(out var state) || ideo.Facts.IdeoId != intent.IdeoId)
        {
            return Unknown<bool>();
        }

        if (state.ReformCount != intent.Count + 1)
        {
            return Known(false);
        }

        try
        {
            var canonical = IdeoligionDesign.CanonicalIdeoligionDesign(actual with { Fluid = state.Fluid });
            return Known(canonical == intent.Design);
        }
        catch (DomainException)
        {
            return Unknown<bool>();
        }
    }
}
