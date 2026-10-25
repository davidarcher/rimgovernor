using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>
    /// The reform the colony's ideoligion owes. An unread ideoligion, catalog, pawn frame, precept def, emergency
    /// snapshot, option set or believer census, or colonists of different or no factions, raises
    /// <see cref="RequiredFactException"/>.
    /// </summary>
    internal static DesignChoice ReformChoice(ReviewReading read, GenerationSnapshot snapshot, IReadOnlyList<IdeoGoalTarget> targets)
    {
        var ideo = read.Reading.Facts.Ideology is IdeologyMode.Installed { Primary: var held } ? held : throw new RequiredFactException("ideoligion");
        var catalog = read.Catalog ?? throw new RequiredFactException("definition catalog");
        var frame = read.Frame.Pawns ?? throw new RequiredFactException("pawn frame");

        var faction = "";
        foreach (var pawn in frame.Pawns)
        {
            if (!pawn.FreeColonist || pawn.Dead)
            {
                continue;
            }

            var name = pawn.FactionDefName ?? "";
            if (name.Length == 0 || (faction.Length != 0 && faction != name))
            {
                throw new RequiredFactException("colonist faction");
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
            throw new RequiredFactException("ideoligion design options");
        }

        var current = CurrentDesign(ideo, catalog);
        var tick = read.Reading.Identity.Tick;
        bool calm;
        try
        {
            var emergency = EmergencySnapshot.New(snapshot, tick, read.Emergency);
            var (hostiles, patients) = EmergencyNeedsRules.EmergencyNeeds(emergency, snapshot, tick);
            calm = RitualRules.RitualCalm(hostiles, patients);
        }
        catch (DomainException)
        {
            throw new RequiredFactException("emergency snapshot");
        }

        // The believers' needs are read only when the improvement search runs.
        DesignNeeds Needs() => IdeoligionDesignRules.IdeoligionNeeds(
            ideo.Facts,
            read.Reading.Facts.ForecastObserved.WorkPawns.Pawns,
            read.Reading.Facts.MoodPawns);
        return IdeoligionDesignRules.ChooseIdeoligionReform(options, current, ideo.Facts.Development, calm, Needs, targets);
    }

    /// <summary>The ideoligion's current design: its memes and the plain precepts the catalog resolves. A precept whose def the catalog lacks raises <see cref="RequiredFactException"/>.</summary>
    internal static IdeoligionDesign CurrentDesign(Ideoligion ideo, DefinitionCatalog catalog)
    {
        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = catalog.DefRow<RimWorld.PreceptDef>(held.DefName) ?? throw new RequiredFactException("precept def");
            if (def.preceptClass == typeof(RimWorld.Precept))
            {
                precepts.Add(held.DefName);
            }
        }

        return new IdeoligionDesign([.. ideo.Facts.Memes], [.. precepts], true, [.. ideo.Facts.VeneratedAnimals], [.. ideo.Facts.StyleCategories]);
    }
}

partial class ColonyReview
{
    /// <summary>Whether the colony's ideoligion now matches the intended design: false while the reform count has not advanced, for another ideoligion, or for a design that has no canonical form.</summary>
    internal static bool ObservedReform(Ideoligion ideo, IdeoligionDesign actual, IdeoligionReform intent)
    {
        var state = ideo.Facts.Development;
        if (ideo.Facts.IdeoId != intent.IdeoId || state is not RimGovernor.Brain.GameFacts.Observations.IdeoDevelopment.Fluid fluid || fluid.ReformCount != intent.Count + 1)
        {
            return false;
        }

        try
        {
            var canonical = IdeoligionDesign.CanonicalIdeoligionDesign(actual with { Fluid = true });
            return canonical == intent.Design;
        }
        catch (DomainException)
        {
            return false;
        }
    }
}
