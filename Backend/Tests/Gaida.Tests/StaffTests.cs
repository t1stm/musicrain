using Gaida.Admin;

namespace Gaida.Tests;

/// <summary>The vocabulary Dom stores and Oko enforces: who may do what.</summary>
public class StaffTests
{
    [Fact]
    public void TheOwnerMayDoWhatNobodyNamedAPermissionFor() =>
        Assert.True(Staff.Can(Staff.Owner, [], null));

    [Fact]
    public void AnAdminMayNotDoWhatNobodyNamedAPermissionFor() =>
        Assert.False(Staff.Can(Staff.Admin, [.. Staff.Permissions.Keys], null));

    [Fact]
    public void AnAdminMayDoOnlyWhatWasGranted()
    {
        Assert.True(Staff.Can(Staff.Admin, ["caches"], "caches"));
        Assert.False(Staff.Can(Staff.Admin, ["caches"], "rooms"));
    }

    [Fact]
    public void AnAccountWithNoRoleMayDoNothing()
    {
        Assert.False(Staff.Can(null, ["caches"], "caches"));
        Assert.False(Staff.Can(null, [], null));
        Assert.False(Staff.Can("someone", ["caches"], "caches"));
    }

    [Fact]
    public void EffectiveGivesTheOwnerEverythingAndAnAdminOnlyKnownGrants()
    {
        Assert.Equal(Staff.Permissions.Keys, Staff.Effective(Staff.Owner, []));
        Assert.Equal(["caches", "studio"], Staff.Effective(Staff.Admin, ["studio", "retired", "caches", "caches"]));
        Assert.Empty(Staff.Effective(null, ["caches"]));
    }
}
