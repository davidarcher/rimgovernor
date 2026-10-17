using RimGovernor.Contracts.Wire.Common;
using DomainCorpseOf = RimGovernor.Brain.Domain.CorpseOf;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go corpse_class.go.
public static partial class BridgeFunctions
{
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
