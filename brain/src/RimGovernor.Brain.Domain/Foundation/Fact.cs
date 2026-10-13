using System.Diagnostics.CodeAnalysis;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// An observation that may be unknown. The default value is unknown, including for boolean and
/// numeric observations (Go <c>Fact[T]</c>).
/// </summary>
public readonly record struct Fact<T>
{
    private readonly T? _value;

    internal Fact(T value)
    {
        _value = value;
        IsKnown = true;
    }

    public bool IsKnown { get; }

    /// <summary>Go's <c>v, ok := f.Value()</c>.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsKnown;
    }

    /// <summary>The value when known, otherwise the type's default (Go's zero value).</summary>
    public T? ValueOrDefault => _value;

    /// <summary>The value; throws when unknown. For code that has already checked <see cref="IsKnown"/>.</summary>
    public T Value => IsKnown ? _value! : throw new DomainException("fact is unknown");

    public override string ToString() => IsKnown ? $"Known({_value})" : "Unknown";
}

/// <summary>Constructors for <see cref="Fact{T}"/>; the project-wide <c>using static</c> makes them read as Go's <c>Known(v)</c> and <c>Unknown[T]()</c>.</summary>
public static class Fact
{
    public static Fact<T> Known<T>(T value) => new(value);

    public static Fact<T> Unknown<T>() => default;
}
