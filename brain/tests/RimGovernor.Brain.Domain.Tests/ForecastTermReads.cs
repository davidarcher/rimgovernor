using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Reads a consequence term the test expects the forecast to have assessed; an unassessed term fails with its reason.</summary>
internal static class ForecastTermReads
{
    public static double Number(this ForecastTerm term)
        => term is ForecastTerm.Assessed(var value) ? value : throw new Xunit.Sdk.XunitException("unassessed: " + ((ForecastTerm.Unassessed)term).Reason);

    public static bool Assessed(this ForecastTerm term) => term is ForecastTerm.Assessed;
}
