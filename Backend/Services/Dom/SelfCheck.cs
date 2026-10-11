using System.Text;
using Dom.Store;
using Microsoft.Data.Sqlite;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Dom;

/// <summary>
///     The pieces of Dom worth proving runnable: the password path, the token path, and the fact that
///     the database it writes reloads into the same accounts. Run with
///     <c>dotnet run --project Dom -- --self-check</c>.
/// </summary>
/// <remarks>
///     Same reasoning as Dunav's: the deployed image carries no test project, and a service that
///     cannot verify a password or reload its own file is a service that cannot serve.
/// </remarks>
internal static class SelfCheck
{
    /// <summary>Silent: the store logs a line per register, and the check's own output is the point.</summary>
    private static readonly ILogger Quiet = new LoggerConfiguration().CreateLogger();

    public static Task<bool> RunAsync()
    {
        var ok = Accounts() & Tokens() & Persistence() & Expiry() & PlaylistsCheck();

        return Task.FromResult(ok);
    }

    /// <summary>Registration rules, and that a password only verifies against itself.</summary>
    private static bool Accounts()
    {
        using var scratch = new ScratchFile();
        var store = new DomStore(scratch.Path, Quiet);

        var created = store.Register("Радост", "correct horse battery");
        var sameNameOtherCase = store.Register("радост", "another password entirely");
        var shortPassword = store.Register("kris", "short");
        var spaced = store.Register("two words", "correct horse battery");

        var wrongPassword = store.Login("Радост", "correct horse batteri");
        var noSuchUser = store.Login("nobody", "correct horse battery");
        var right = store.Login("РАДОСТ", "correct horse battery");

        var ok = created.Ok
                 && sameNameOtherCase.Refusal?.Code == "username_taken"
                 && shortPassword.Refusal?.Code == "invalid_request"
                 && spaced.Refusal?.Code == "invalid_request"
                 && wrongPassword.Refusal?.Code == "invalid_credentials"
                 // a wrong password and a missing account must be indistinguishable to the caller
                 && noSuchUser.Refusal?.Code == wrongPassword.Refusal?.Code
                 && noSuchUser.Refusal?.Message == wrongPassword.Refusal?.Message
                 && right.Ok
                 && store.UserCount == 1;

        Report(ok, "registration rules hold and a password verifies only against itself",
            $"created={created.Refusal?.Code ?? "ok"} duplicate={sameNameOtherCase.Refusal?.Code} " +
            $"weak={shortPassword.Refusal?.Code} spaced={spaced.Refusal?.Code} wrong={wrongPassword.Refusal?.Code} " +
            $"missing={noSuchUser.Refusal?.Code} login={right.Refusal?.Code ?? "ok"} users={store.UserCount}");

        return ok;
    }

    /// <summary>A token identifies its own account, and signing out on one device leaves the other in.</summary>
    private static bool Tokens()
    {
        using var scratch = new ScratchFile();
        var store = new DomStore(scratch.Path, Quiet);

        var phone = store.Register("kris", "correct horse battery").Value!.Token;
        var laptop = store.Login("kris", "correct horse battery").Value!.Token;

        var distinct = phone.Value != laptop.Value;
        var both = store.Resolve(phone.Value)?.Username == "kris" && store.Resolve(laptop.Value)?.Username == "kris";

        store.Logout(phone.Value);

        var ok = distinct
                 && both
                 && store.Resolve(phone.Value) is null
                 && store.Resolve(laptop.Value)?.Username == "kris"
                 && store.Resolve("not-a-token") is null
                 && store.Resolve(null) is null;

        Report(ok, "one token per sign-in, and signing out revokes only that one",
            $"distinct={distinct} bothResolved={both}");

        return ok;
    }

    /// <summary>The database is the account. A restart has to be invisible.</summary>
    private static bool Persistence()
    {
        using var scratch = new ScratchFile();

        string token;
        {
            var store = new DomStore(scratch.Path, Quiet);
            token = store.Register("Радост", "correct horse battery").Value!.Token.Value;
        }

        var reloaded = new DomStore(scratch.Path, Quiet);

        var ok = reloaded.UserCount == 1
                 && reloaded.Resolve(token)?.Username == "Радост"
                 && reloaded.Login("радост", "correct horse battery").Ok
                 && reloaded.Login("радост", "wrong").Refusal?.Code == "invalid_credentials"
                 // the password itself must not be anywhere on disk
                 && !scratch.Bytes().Contains("correct horse battery");

        Report(ok, "accounts, tokens and password hashes survive a restart",
            $"users={reloaded.UserCount} tokenResolved={reloaded.Resolve(token) is not null}");

        return ok;
    }

    /// <summary>An expired token is not a token. Aged in the database, because that is where the expiry lives.</summary>
    private static bool Expiry()
    {
        using var scratch = new ScratchFile();

        string token;
        {
            var store = new DomStore(scratch.Path, Quiet);
            token = store.Register("kris", "correct horse battery").Value!.Token.Value;
        }

        scratch.Execute("UPDATE tokens SET ExpiresUtc = $at", DateTimeOffset.UtcNow.AddMinutes(-1));

        var store2 = new DomStore(scratch.Path, Quiet);
        var rejected = store2.Resolve(token) is null;

        // the rejection also drops it, so the database no longer carries a token nobody can use
        var pruned = (long)scratch.Execute("SELECT COUNT(*) FROM tokens WHERE Value = $at", token)! == 0;
        var stillSignsIn = store2.Login("kris", "correct horse battery").Ok;

        var ok = rejected && pruned && stillSignsIn;

        Report(ok, "an expired token is rejected, dropped, and does not cost the account",
            $"rejected={rejected} pruned={pruned} canSignInAgain={stillSignsIn}");

        return ok;
    }

    /// <summary>Ownership and visibility, which are the whole of what a playlist promises.</summary>
    private static bool PlaylistsCheck()
    {
        using var scratch = new ScratchFile();
        var store = new DomStore(scratch.Path, Quiet);

        var kris = store.Register("kris", "correct horse battery").Value!.User;
        var stranger = store.Register("stranger", "correct horse battery").Value!.User;

        List<TrackSnapshot> tracks =
        [
            new() { Id = "local://1", Name = "Ноќ", Artist = "Someone", Duration = "00:03:41" },
            new() { Id = "yt://abc", Name = "Another", Artist = "Someone else", Duration = "00:04:02" }
        ];

        var creation = store.Create(kris, "Late shift", Visibility.Private, tracks);
        var (made, error) = (creation.Value, creation.Refusal?.Code);
        var unnamed = store.Create(kris, "   ", Visibility.Private, null);

        var mineOnly = store.Mine(kris).Count == 1 && store.Public().Count == 0;
        var hidden = store.Visible(made!.Id, stranger) is null && store.Visible(made.Id, kris) is not null;

        store.Update(kris, made.Id, null, Visibility.Public, null);
        var shared = store.Public().Count == 1 && store.Visible(made.Id, stranger) is not null;

        // a playlist is only ever changed or removed by whoever owns it — seeing it is not enough
        var strangerPatch = store.Update(stranger, made.Id, "Mine now", null, null).Refusal?.Code;
        var strangerDelete = store.Delete(stranger, made.Id).deleted;

        var duration = store.Visible(made.Id, kris)!.Duration == TimeSpan.FromSeconds(3 * 60 + 41 + 4 * 60 + 2);

        var reloaded = new DomStore(scratch.Path, Quiet);
        var survived = reloaded.Public().Count == 1 && reloaded.Visible(made.Id, null)!.Tracks.Count == 2;

        var gone = reloaded.Delete(kris, made.Id).deleted && reloaded.Mine(kris).Count == 0;

        var ok = error is null
                 && unnamed.Refusal?.Code == "invalid_request"
                 && mineOnly && hidden && shared && duration && survived && gone
                 && strangerPatch == "forbidden"
                 && !strangerDelete;

        Report(ok, "a playlist is private until it is not, and only its owner can change it",
            $"created={error ?? "ok"} unnamed={unnamed.Refusal?.Code} mineOnly={mineOnly} hidden={hidden} " +
            $"shared={shared} duration={duration} reloaded={survived} deleted={gone} " +
            $"strangerPatch={strangerPatch} strangerDeleted={strangerDelete}");

        return ok;
    }

    private static void Report(bool ok, string claim, string detail) =>
        Console.WriteLine(ok ? $"OK: {claim}" : $"FAIL: {claim} — {detail}");

    /// <summary>A throwaway accounts database, so a self-check run never touches a real one.</summary>
    private sealed class ScratchFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dom-selfcheck-" + Guid.NewGuid().ToString("n") + ".db");

        /// <summary>Everything SQLite has written, the log included, as text to search.</summary>
        public string Bytes() => string.Concat(new[] { Path, Path + "-wal" }.Where(File.Exists)
            .Select(file => Encoding.UTF8.GetString(File.ReadAllBytes(file))));

        /// <summary>One statement with one parameter, straight at the file, the way an operator would.</summary>
        public object? Execute(string sql, object at)
        {
            using var db = new SqliteConnection($"Data Source={Path}");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$at", at);
            return command.ExecuteScalar();
        }

        public void Dispose()
        {
            try
            {
                foreach (var suffix in (string[])["", "-wal", "-shm"]) File.Delete(Path + suffix);
            }
            catch (IOException)
            {
                // a leftover scratch file in the temp directory is not worth failing a check over
            }
        }
    }
}
