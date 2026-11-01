using Dom.Store;
using Gaida.Admin;
using Microsoft.Data.Sqlite;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>
///     Roles, the administrator key and the Staff routes' store half. The key is the one thing in Dom that
///     turns an ordinary account into one that can change other people's, so every way it could be issued
///     twice, outlive its claim or be claimed by the wrong caller has a case here.
/// </summary>
public class DomStaffTests : IDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _directory = Directory.CreateTempSubdirectory("dom-staff").FullName;

    private string KeyFile => Path.Combine(_directory, "admin.key");

    public void Dispose()
    {
        Directory.Delete(_directory, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ANewDatabaseIssuesAKeyNobodyElseCanRead()
    {
        Store();

        var key = File.ReadAllText(KeyFile);
        Assert.Matches("^[0-9a-f]{64}$", key);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
    }

    [Fact]
    public void ALaterBootIssuesNoKey()
    {
        var store = Store();
        var key = File.ReadAllText(KeyFile);

        Store();
        Assert.Equal(key, File.ReadAllText(KeyFile));

        Assert.True(store.ClaimStaff(User(store, "kris"), key).Ok);
        Store();
        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public void TheFlagIssuesAKeyOnlyWhenNoneIsWaiting()
    {
        var store = Store();
        var key = File.ReadAllText(KeyFile);

        Store(newAdminKey: true);
        Assert.Equal(key, File.ReadAllText(KeyFile));

        Assert.True(store.ClaimStaff(User(store, "kris"), key).Ok);
        Store(newAdminKey: true);
        Assert.NotEqual(key, File.ReadAllText(KeyFile));
    }

    /// <summary>The live database is at version 2: the upgrade keeps every account and issues the first key.</summary>
    [Fact]
    public void AVersionTwoDatabaseUpgradesAndIssuesAKey()
    {
        var store = Store();
        var token = store.Register("kris", Password).Value!.Token.Value;
        var id = store.Resolve(token)!.Id;
        File.Delete(KeyFile);

        // back to what a version 2 database looked like
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_directory, "dom.db")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText =
                "ALTER TABLE users DROP COLUMN Role; ALTER TABLE users DROP COLUMN Permissions; PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        var upgraded = Store();
        Assert.Equal(id, upgraded.Resolve(token)?.Id);
        Assert.Null(upgraded.Resolve(token)!.Role);
        Assert.Matches("^[0-9a-f]{64}$", File.ReadAllText(KeyFile));
    }

    [Fact]
    public void TheFirstClaimMakesTheOwnerAndALaterKeyAnAdmin()
    {
        var store = Store();
        var key = File.ReadAllText(KeyFile);
        var kris = User(store, "kris");
        var ana = User(store, "ana");

        var claimed = store.ClaimStaff(kris, key);
        Assert.Equal("owner", claimed.Value?.Role);
        Assert.Equal(Staff.Permissions.Keys, claimed.Value!.Permissions);
        Assert.False(File.Exists(KeyFile));

        // spent: the same key from somebody else is just a wrong key
        Assert.Equal("invalid_key", store.ClaimStaff(ana, key).Refusal?.Code);
        Assert.Equal("already_staff", store.ClaimStaff(kris, key).Refusal?.Code);

        var reissued = Store(newAdminKey: true);
        var later = reissued.ClaimStaff(reissued.Login("ana", Password).Value!.User, File.ReadAllText(KeyFile));
        Assert.Equal(("admin", 0), (later.Value?.Role, later.Value?.Permissions.Length));
    }

    [Fact]
    public void AWrongKeyLeavesTheRightOneWaiting()
    {
        var store = Store();
        var key = File.ReadAllText(KeyFile);
        var kris = User(store, "kris");

        Assert.Equal("invalid_key", store.ClaimStaff(kris, new string('0', 64)).Refusal?.Code);
        Assert.Equal("invalid_key", store.ClaimStaff(kris, null).Refusal?.Code);
        Assert.Equal(key, File.ReadAllText(KeyFile));

        // pasted with the line end a terminal copy brings along
        Assert.True(store.ClaimStaff(kris, $" {key}\n").Ok);
    }

    [Fact]
    public void OnlyTheOwnerManagesStaff()
    {
        var (store, owner) = Owned();
        var ana = User(store, "ana");
        var mila = User(store, "mila");

        Assert.True(store.GrantStaff(owner, "ana", ["caches"]).Ok);

        // an admin can do none of it, not even to themselves
        Assert.Equal("forbidden", store.StaffList(ana).Refusal?.Code);
        Assert.Equal("forbidden", store.GrantStaff(ana, "mila", []).Refusal?.Code);
        Assert.Equal("forbidden", store.GrantStaff(ana, "ana", ["rooms"]).Refusal?.Code);
        Assert.Equal("forbidden", store.DemoteStaff(ana, "ana")?.Code);
        Assert.Equal("forbidden", store.StaffList(mila).Refusal?.Code);

        Assert.Equal(["kris", "ana"], store.StaffList(owner).Value!.Select(member => member.Username));
        Assert.Null(store.DemoteStaff(owner, "ana"));
        Assert.Null(ana.Role);
        Assert.Empty(ana.Permissions);
        Assert.Equal("not_staff", store.DemoteStaff(owner, "ana")?.Code);
    }

    [Fact]
    public void GrantsAreCheckedAndTheOwnerIsFixed()
    {
        var (store, owner) = Owned();
        User(store, "ana");

        Assert.Equal("unknown_permission", store.GrantStaff(owner, "ana", ["caches", "everything"]).Refusal?.Code);
        Assert.Equal("invalid_request", store.GrantStaff(owner, "ana", null).Refusal?.Code);
        Assert.Equal("no_such_account", store.GrantStaff(owner, "nobody", []).Refusal?.Code);
        Assert.Equal("owner_fixed", store.GrantStaff(owner, "kris", []).Refusal?.Code);
        Assert.Equal("owner_fixed", store.DemoteStaff(owner, "KRIS")?.Code);
        Assert.Equal("owner", owner.Role);

        // promotes and edits alike, in the vocabulary's order, once each
        Assert.Equal(["library", "studio"], store.GrantStaff(owner, "ana", ["studio", "library", "studio"]).Value!.Permissions);
        Assert.Empty(store.GrantStaff(owner, "ana", []).Value!.Permissions);
    }

    [Fact]
    public void RolesSurviveARestartAndARename()
    {
        var (store, owner) = Owned();
        var ana = User(store, "ana");
        store.GrantStaff(owner, "ana", ["rooms", "bot"]);
        Assert.Null(store.Rename(ana, Password, "Anna"));
        Assert.Null(store.Rename(owner, Password, "Kristian"));

        var reloaded = Store();
        var anna = reloaded.Login("anna", Password).Value!.User;
        Assert.Equal("admin", anna.Role);
        Assert.Equal(["rooms", "bot"], anna.Permissions);
        Assert.Equal("owner", reloaded.Login("kristian", Password).Value!.User.Role);
    }

    /// <summary>The escalation the gateway would otherwise open: an Admin with <c>accounts</c> resetting the Owner.</summary>
    [Fact]
    public void OperatorActionsLeaveStaffAccountsAlone()
    {
        var (store, owner) = Owned();
        User(store, "ana");
        store.GrantStaff(owner, "ana", []);
        var hash = owner.Hash;

        Assert.False(store.AdminSetPassword("kris", "a password of mine").ok);
        Assert.False(store.AdminRenameUser("ana", "taken over").ok);
        Assert.NotNull(store.AdminSignOut("kris").error);
        Assert.NotNull(store.AdminDeleteUser("ana").error);

        Assert.Equal(hash, owner.Hash);
        Assert.True(store.Login("kris", Password).Ok);
        Assert.True(store.Login("ana", Password).Ok);
        Assert.NotEmpty(owner.Tokens);
    }

    /// <summary>A grant Dom no longer knows the name of, left in the column by an older version, is no grant.</summary>
    [Fact]
    public void ANameDroppedFromTheVocabularyIsRevoked()
    {
        var (store, owner) = Owned();
        User(store, "ana");
        store.GrantStaff(owner, "ana", ["caches"]);

        using (var db = new SqliteConnection($"Data Source={Path.Combine(_directory, "dom.db")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE users SET Permissions = 'caches retired' WHERE Key = 'ana'";
            command.ExecuteNonQuery();
        }

        var ana = Store().Login("ana", Password).Value!.User;
        Assert.Equal(["caches"], Staff.Effective(ana.Role, ana.Permissions));
        Assert.Equal(Staff.Permissions.Keys, Staff.Effective(owner.Role, owner.Permissions));
    }

    /// <summary>A store whose key <c>kris</c> has already claimed.</summary>
    private (DomStore store, User owner) Owned()
    {
        var store = Store();
        var owner = User(store, "kris");
        Assert.True(store.ClaimStaff(owner, File.ReadAllText(KeyFile)).Ok);
        return (store, owner);
    }

    private static User User(DomStore store, string username) =>
        store.Resolve(store.Register(username, Password).Value!.Token.Value)!;

    private DomStore Store(bool newAdminKey = false) =>
        new(Path.Combine(_directory, "dom.db"), Logger.None, newAdminKey);
}
