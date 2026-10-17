using System.Diagnostics.CodeAnalysis;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// A domain invariant was violated: an error from a constructor, validator or state
/// transition. Failed transitions leave the original immutable value untouched.
/// </summary>
public class DomainException(string message) : Exception(message)
{
    /// <summary>Throws <see cref="DomainException"/> unless <paramref name="condition"/> holds.</summary>
    public static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new DomainException(message);
        }
    }
}

/// <summary>An action prerequisite has not completed in the current world.</summary>
public sealed class DependencyException() : DomainException("action prerequisite has not completed in the current world");

/// <summary>
/// A native write failed before the call was issued. The transport throws it
/// (wrapping the cause); executors record <see cref="Receipt.Unsent"/> instead of an uncertain receipt.
/// </summary>
public sealed class WriteUnsentException : DomainException
{
    public WriteUnsentException()
        : base("native write never sent") { }

    public WriteUnsentException(Exception inner)
        : base("native write never sent: " + inner.Message) { }
}
