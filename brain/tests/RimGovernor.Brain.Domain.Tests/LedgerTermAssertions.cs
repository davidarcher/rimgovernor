using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Reads a ledger term in a test: its loss when priced (failing otherwise), or whether it was priced.</summary>
internal static class LedgerTermAssertions
{
    public static double Loss(this LedgerTerm term) => Assert.IsType<LedgerTerm.Priced>(term).Loss;
    public static bool IsPriced(this LedgerTerm term) => term is LedgerTerm.Priced;
}
