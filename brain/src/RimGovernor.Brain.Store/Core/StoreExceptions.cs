using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>A plan or action identity already exists, or a compare-and-swap lost.</summary>
public class ConflictException : DomainException
{
    public ConflictException()
        : base("plan or action identity already exists") { }

    public ConflictException(Exception inner)
        : base("plan or action identity already exists: " + inner.Message) { }

    public ConflictException(string detail)
        : base("plan or action identity already exists: " + detail) { }
}

/// <summary>A commit refused because the owner already binds that method id.</summary>
public sealed class MethodBoundException(string detail) : ConflictException("method already bound: " + detail);

/// <summary>
/// A combat commit, a Need plan's dispatch or an activity continuation the Safeguards or shared admission refuse. A
/// claimed commit returns its refusal (<see cref="ClaimOutcome.Refused"/>) instead.
/// </summary>
public sealed class NotAdmittedException : DomainException
{
    public NotAdmittedException()
        : base("method not admitted") { }

    public NotAdmittedException(string detail)
        : base("method not admitted: " + detail) { }
}

public sealed class NotFoundException() : DomainException("plan or action not found");

public sealed class CapacityException(string detail = "") : DomainException(detail.Length == 0
    ? "plan catalog capacity reached" : "journal capacity reached: " + detail);

/// <summary>An action Safeguard's dispatch refusal.</summary>
public sealed class ActionVetoedException : DomainException
{
    public ActionVetoedException()
        : base("action vetoed") { }

    public ActionVetoedException(string detail)
        : base("action vetoed: " + detail) { }
}

/// <summary>Native postconditions must settle existing work before another method is admitted.</summary>
/// <summary>The combat Need admits no Method beside its own open work or a held fight.</summary>
public sealed class CombatWorkOpenException() : DomainException("existing combat method requires observation");

/// <summary>
/// <see cref="Store.Open"/> refuses a database another schema version or
/// application wrote. <see cref="Found"/> is the <c>application/version</c> pair it carried.
/// </summary>
public sealed class IncompatibleDatabaseException(string found)
    : DomainException("incompatible database application/version: " + found)
{
    public string Found { get; } = found;
}
