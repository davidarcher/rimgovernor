using RimGovernor.Protocol.Common;
using DomainCorpseOf = RimGovernor.Brain.Domain.CorpseOf;
using DomainRotStage = RimGovernor.Brain.Domain.RotStage;
using WireRotStage = RimGovernor.Protocol.Common.RotStage;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go corpse_class.go.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>CorpseOf</c>: the domain class of a wire class; <c>None</c> (Go's empty, invalid) for unspecified or unknown.</summary>
    public static DomainCorpseOf CorpseOf(CorpseClass value) =>
        value switch
        {
            CorpseClass.Colonist => DomainCorpseOf.Colonist,
            CorpseClass.Stranger => DomainCorpseOf.Stranger,
            CorpseClass.Animal => DomainCorpseOf.Animal,
            _ => DomainCorpseOf.None,
        };

    /// <summary>Go's <c>RotOf</c>: the domain stage of a wire rot stage; <c>None</c> for unspecified or unknown.</summary>
    public static DomainRotStage RotOf(WireRotStage value) =>
        value switch
        {
            WireRotStage.Fresh => DomainRotStage.Fresh,
            WireRotStage.Rotting => DomainRotStage.Rotting,
            WireRotStage.Dessicated => DomainRotStage.Dessicated,
            _ => DomainRotStage.None,
        };

    /// <summary>Go's <c>CorpseClass</c>: the wire class of a domain class.</summary>
    public static CorpseClass WireCorpseClass(DomainCorpseOf value) =>
        value switch
        {
            DomainCorpseOf.Colonist => CorpseClass.Colonist,
            DomainCorpseOf.Stranger => CorpseClass.Stranger,
            DomainCorpseOf.Animal => CorpseClass.Animal,
            _ => CorpseClass.Unspecified,
        };
}
