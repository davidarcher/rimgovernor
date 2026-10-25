using RimGovernor.Brain.GameFacts.Common;
using DomainCorpseOf = RimGovernor.Brain.Domain.CorpseOf;

namespace RimGovernor.Brain.GameAccess;

public static class CorpseClassRules
{
    /// <summary>The wire class of a domain class; <c>None</c> names no class, so it has no wire class.</summary>
    public static CorpseClass WireCorpseClass(DomainCorpseOf value)
    {
        if (value == DomainCorpseOf.Colonist)
        {
            return CorpseClass.Colonist;
        }

        if (value == DomainCorpseOf.Stranger)
        {
            return CorpseClass.Stranger;
        }

        if (value == DomainCorpseOf.Animal)
        {
            return CorpseClass.Animal;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "no corpse class to send");
    }
}
