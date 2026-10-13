using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>Go <c>core.ErrConflict</c>: a plan or action identity already exists, or a compare-and-swap lost.</summary>
public class ConflictException : DomainException
{
    public ConflictException()
        : base("plan or action identity already exists") { }

    public ConflictException(Exception inner)
        : base("plan or action identity already exists: " + inner.Message) { }

    public ConflictException(string detail)
        : base("plan or action identity already exists: " + detail) { }
}

/// <summary>Go <c>ErrStaleOwner</c>: another planner changed the method's owner after this one read it.</summary>
public sealed class StaleOwnerException(string detail) : ConflictException("owner changed since it was read: " + detail);

/// <summary>Go <c>ErrMethodBound</c>: a commit refused because the owner already binds that method id.</summary>
public sealed class MethodBoundException(string detail) : ConflictException("method already bound: " + detail);

/// <summary>Go <c>core.ErrNotAdmitted</c>: a method without a current enabled review.</summary>
public sealed class NotAdmittedException : DomainException
{
    public NotAdmittedException()
        : base("standard method not admitted") { }

    public NotAdmittedException(string detail)
        : base("standard method not admitted: " + detail) { }
}

/// <summary>Go <c>core.ErrNotFound</c>.</summary>
public sealed class NotFoundException() : DomainException("plan or action not found");

/// <summary>Go <c>core.ErrCapacity</c>.</summary>
public sealed class CapacityException() : DomainException("plan catalog capacity reached");

/// <summary>Go <c>ErrActionVetoed</c>: an action Safeguard's dispatch refusal.</summary>
public sealed class ActionVetoedException : DomainException
{
    public ActionVetoedException()
        : base("action vetoed") { }

    public ActionVetoedException(string detail)
        : base("action vetoed: " + detail) { }
}

/// <summary>Go <c>ErrOpenMethod</c>: native postconditions must settle existing work before another method is admitted.</summary>
public sealed class OpenMethodException() : DomainException("existing method requires observation");

/// <summary>
/// Go <c>ErrIncompatible</c>: <see cref="Store.Open"/> refuses a database another schema version or
/// application wrote. <see cref="Found"/> is the <c>application/version</c> pair it carried.
/// </summary>
public sealed class IncompatibleDatabaseException(string found)
    : DomainException("incompatible database application/version: " + found)
{
    public string Found { get; } = found;
}
