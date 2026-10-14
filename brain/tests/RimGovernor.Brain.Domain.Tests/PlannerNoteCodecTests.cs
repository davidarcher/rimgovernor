using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The zero planner note (<c>new PlannerNote()</c>) carried a null Subject that the journal wrote as null and then refused to read back.</summary>
public sealed class PlannerNoteCodecTests
{
    [Fact]
    public void DefaultNoteHasEmptySubjectAndEqualsTheExplicitZeroNote()
    {
        var zero = new PlannerNote();
        Assert.Equal("", zero.Subject);
        Assert.Equal(new PlannerNote(Cause.None, ""), zero);
    }

    [Fact]
    public void NullStringFieldReadsBackAsEmpty()
    {
        var json = StoreJson.Serialize(new ConcernProgress { PlannerSubject = null! });
        Assert.Equal("", StoreJson.Deserialize<ConcernProgress>(json).PlannerSubject);
    }
}
