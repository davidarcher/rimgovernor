namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The acceptance case registry (Go <c>cases.Register/All/Lookup</c>). Cases live in the Acceptance.Cases project and
/// register through an explicit call on a <see cref="CaseRegistry"/> (no <c>init()</c> in C#): the project's
/// <c>CaseCatalog.Register(CaseRegistry)</c> is the entry point the <c>acceptance</c> command calls.
/// </summary>
public sealed class CaseRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AcceptanceCase> _cases = new(StringComparer.Ordinal);

    /// <summary>Adds <paramref name="c"/>; throws on an invalid case or a duplicate name.</summary>
    public void Register(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Validate();
        lock (_gate)
        {
            if (!_cases.TryAdd(c.Name, c))
            {
                throw new AcceptanceException($"case {c.Name} registered twice");
            }
        }
    }

    /// <summary>Every registered case sorted by name.</summary>
    public IReadOnlyList<AcceptanceCase> All()
    {
        lock (_gate)
        {
            var all = new List<AcceptanceCase>(_cases.Values);
            all.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return all;
        }
    }

    /// <summary>The case registered as <paramref name="name"/>, or null.</summary>
    public AcceptanceCase? Lookup(string name)
    {
        lock (_gate)
        {
            return _cases.GetValueOrDefault(name);
        }
    }

    /// <summary>
    /// Resolves names to registry cases in registry order: a bare name is one case, "&lt;area&gt;/..." every case of the
    /// area, and no names the whole registry. An unmatched name throws.
    /// </summary>
    public IReadOnlyList<AcceptanceCase> Select(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        IReadOnlyList<AcceptanceCase> all = All();
        if (names.Count == 0)
        {
            return all;
        }

        var want = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            bool matched = false;
            foreach (AcceptanceCase c in all)
            {
                if (c.Name == name || (name.EndsWith("/...", StringComparison.Ordinal) && c.Name.StartsWith(name[..^3], StringComparison.Ordinal)))
                {
                    want.Add(c.Name);
                    matched = true;
                }
            }

            if (!matched)
            {
                throw new AcceptanceException($"unknown case \"{name}\" (see `acceptance list`)");
            }
        }

        return all.Where(c => want.Contains(c.Name)).ToList();
    }
}
