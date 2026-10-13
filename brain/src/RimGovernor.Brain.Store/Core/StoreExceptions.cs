using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>Go <c>core.ErrConflict</c>: a plan or action identity already exists.</summary>
public sealed class ConflictException : DomainException
{
    public ConflictException()
        : base("plan or action identity already exists") { }

    public ConflictException(Exception inner)
        : base("plan or action identity already exists: " + inner.Message) { }
}

/// <summary>Go <c>core.ErrNotAdmitted</c>: a method without a current enabled review.</summary>
public sealed class NotAdmittedException() : DomainException("standard method not admitted");

/// <summary>Go <c>core.ErrNotFound</c>.</summary>
public sealed class StoreNotFoundException() : DomainException("plan or action not found");

/// <summary>Go <c>core.ErrCapacity</c>.</summary>
public sealed class CapacityException() : DomainException("plan catalog capacity reached");

/// <summary>Go <c>ErrActionVetoed</c>: an action Safeguard's dispatch refusal.</summary>
public sealed class ActionVetoedException() : DomainException("action vetoed");

/// <summary>
/// Go <c>ErrIncompatible</c>: <see cref="StoreDatabase.Open"/> refuses a database another schema version or
/// application wrote. <see cref="Found"/> is the <c>application/version</c> pair it carried.
/// </summary>
public sealed class IncompatibleDatabaseException(string found)
    : DomainException("incompatible database application/version: " + found)
{
    public string Found { get; } = found;
}
