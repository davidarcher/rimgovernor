using System.Globalization;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_surgery_parts.go: the replacement-part, blood-loss and body-part facts of the surgery planners.

public sealed partial class DefinitionCatalog
{
    private const string BloodLossHediff = "BloodLoss";
    private const string BloodLossSeriousStage = "moderate";

    private Lazy<(IReadOnlyList<string> Organs, IReadOnlySet<string> Vital)>? _bodyPartFacts;

    /// <summary>
    /// The capacity the install recipe's part gives back relative to a natural part (1): 1 for a natural install, the
    /// added part hediff's partEfficiency for an artificial one, 0 for a recipe that installs no replacement part.
    /// Every other worker (<c>Recipe_InstallImplant</c>, <c>Recipe_AddHediff</c>) adds no replacement part.
    /// </summary>
    public double PartTier(string recipe)
    {
        var row = Recipe(recipe);
        if (typeof(Recipe_InstallNaturalBodyPart).IsAssignableFrom(row.workerClass))
        {
            return 1;
        }

        return typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(row.workerClass)
            ? HediffPartTier(row.addsHediff?.defName ?? string.Empty)
            : 0;
    }

    /// <summary>The added part hediff's partEfficiency; a hediff with no row or no addedPartProps is an error.</summary>
    public double HediffPartTier(string hediff) =>
        RequireRow<HediffDef>(hediff).addedPartProps is { } added
            ? Decimal32(added.partEfficiency)
            : throw new CatalogException($"hediff {hediff} has no addedPartProps");

    /// <summary>
    /// The BloodLoss severity from which a pawn is seriously bleeding: the minSeverity of the hediff's "moderate"
    /// stage, where blood loss first costs consciousness heavily. Unknown for a catalog with no BloodLoss row; a BloodLoss
    /// row with no such stage is an error.
    /// </summary>
    public Fact<double> SeriousBloodLoss()
    {
        if (DefRow<HediffDef>(BloodLossHediff) is not { } row)
        {
            return Unknown<double>();
        }

        return row.stages.Seq().FirstOrDefault(s => s.label == BloodLossSeriousStage) is { } stage
            ? Known(Decimal32(stage.minSeverity))
            : throw new CatalogException($"hediff {BloodLossHediff} has no \"{BloodLossSeriousStage}\" stage");
    }

    /// <summary>The hediff def's isBad; a hediff with no row is an error.</summary>
    public Fact<bool> HediffBad(string hediff) => Known(RequireRow<HediffDef>(hediff).isBad);

    /// <summary>The item a removed part hediff leaves behind (its spawnThingOnRemoved), unknown when it spawns nothing; a hediff with no row is an error.</summary>
    public Fact<P.Resource> HediffSpawnOnRemoved(string hediff) =>
        RequireRow<HediffDef>(hediff).spawnThingOnRemoved is { } item ? Known(new P.Resource(item.defName)) : Unknown<P.Resource>();

    /// <summary>
    /// The harvest and kept-part facts derived from the BodyDef trees and the BodyPartDef rows. A vital BodyPartTagDef
    /// marks a part vital; a part is a vital part when removing one instance kills in every body that has it, and a vital
    /// tag rides on it. Removing one instance of it kills when some vital tag of it is provided by no other part of that
    /// body (a heart is the only blood pump); a vital part whose every vital tag survives the removal of one instance and
    /// which the body carries twice is a paired organ, a harvest organ when it spawns the item of its own defName on
    /// removal (a kidney).
    /// </summary>
    internal (IReadOnlyList<string> HarvestOrgans, IReadOnlySet<string> VitalParts) BodyPartFacts()
    {
        _bodyPartFacts ??= new(ComputeBodyPartFacts);
        return _bodyPartFacts.Value;
    }

    private (IReadOnlyList<string> Organs, IReadOnlySet<string> Vital) ComputeBodyPartFacts()
    {
        var vitalTag = Rows<BodyPartTagDef>().Values.Where(t => t.vital).Select(t => t.defName).ToHashSet(StringComparer.Ordinal);
        var tallies = new Dictionary<BodyPartDef, BodyPartTally>();
        foreach (var body in Rows<BodyDef>().Values.OrderBy(b => b.defName, StringComparer.Ordinal))
        {
            var count = new Dictionary<BodyPartDef, int>();
            var provided = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new Stack<BodyPartRecord>();
            stack.Push(body.corePart);
            while (stack.Count > 0)
            {
                var record = stack.Pop();
                count[record.def] = count.GetValueOrDefault(record.def) + 1;
                foreach (var tag in record.def.tags.Seq())
                {
                    provided[tag.defName] = provided.GetValueOrDefault(tag.defName) + 1;
                }

                foreach (var child in record.parts.Seq())
                {
                    stack.Push(child);
                }
            }

            foreach (var (part, n) in count)
            {
                if (!tallies.TryGetValue(part, out var tally))
                {
                    tallies[part] = tally = new BodyPartTally();
                }

                tally.Bodies++;
                tally.Pair |= n >= 2;
                var kills = false;
                foreach (var tag in part.tags.Seq().Where(t => vitalTag.Contains(t.defName)))
                {
                    tally.HasVital = true;
                    kills |= provided.GetValueOrDefault(tag.defName) <= 1;
                }

                if (kills)
                {
                    tally.Killing++;
                }
            }
        }

        var organs = new List<string>();
        var vital = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (part, tally) in tallies)
        {
            if (tally.Killing == tally.Bodies)
            {
                vital.Add(part.defName);
            }
            else if (tally.HasVital && tally.Pair && part.spawnThingOnRemoved?.defName == part.defName)
            {
                organs.Add(part.defName);
            }
        }

        organs.Sort(StringComparer.Ordinal);
        return (organs, vital);
    }

    private sealed class BodyPartTally
    {
        internal int Bodies;
        internal int Killing;
        internal bool HasVital;
        internal bool Pair;
    }

    // Decimal32 widens a def's float to the decimal the XML wrote (0.6, not 0.6000000238).
    internal static double Decimal32(float value) =>
        double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
