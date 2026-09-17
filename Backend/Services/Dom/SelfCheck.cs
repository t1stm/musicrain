using System.Text.Json;
using Dom.Store;
using ILogger = Serilog.ILogger;
using Serilog;

namespace Dom;

/// <summary>
///     The pieces of Dom worth proving runnable: the password path, the token path, and the fact that
///     the file it writes reloads into the same accounts. Run with
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

        var ok = created.token is not null
                 && sameNameOtherCase.error == "username_taken"
                 && shortPassword.error == "invalid_request"
                 && spaced.error == "invalid_request"
                 && wrongPassword.error == "invalid_credentials"
                 // a wrong password and a missing account must be indistinguishable to the caller
                 && noSuchUser.error == wrongPassword.error
                 && noSuchUser.message == wrongPassword.message
                 && right.token is not null
                 && store.UserCount == 1;

        Report(ok, "registration rules hold and a password verifies only against itself",
            $"created={created.error ?? "ok"} duplicate={sameNameOtherCase.error} " +
            $"weak={shortPassword.error} spaced={spaced.error} wrong={wrongPassword.error} " +
            $"missing={noSuchUser.error} login={right.error ?? "ok"} users={store.UserCount}");

        return ok;
    }

    /// <summary>A token identifies its own account, and signing out on one device leaves the other in.</summary>
    private static bool Tokens()
    {
        using var scratch = new ScratchFile();
        var store = new DomStore(scratch.Path, Quiet);

        var phone = store.Register("kris", "correct horse battery").token!;
        var laptop = store.Login("kris", "correct horse battery").token!;

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

    /// <summary>The file is the account. A restart has to be invisible.</summary>
    private static bool Persistence()
    {
        using var scratch = new ScratchFile();

        string token;
        {
            var store = new DomStore(scratch.Path, Quiet);
            token = store.Register("Радост", "correct horse battery").token!.Value;
        }

        var reloaded = new DomStore(scratch.Path, Quiet);

        var ok = reloaded.UserCount == 1
                 && reloaded.Resolve(token)?.Username == "Радост"
                 && reloaded.Login("радост", "correct horse battery").token is not null
                 && reloaded.Login("радост", "wrong").error == "invalid_credentials"
                 // the password itself must not be anywhere in the file
                 && !File.ReadAllText(scratch.Path).Contains("correct horse battery");

        Report(ok, "accounts, tokens and password hashes survive a restart",
            $"users={reloaded.UserCount} tokenResolved={reloaded.Resolve(token) is not null}");

        return ok;
    }

    /// <summary>An expired token is not a token. Aged on disk, because that is where the expiry lives.</summary>
    private static bool Expiry()
    {
        using var scratch = new ScratchFile();

        string token;
        {
            var store = new DomStore(scratch.Path, Quiet);
            token = store.Register("kris", "correct horse battery").token!.Value;
        }

        var state = JsonSerializer.Deserialize<DomState>(File.ReadAllText(scratch.Path))!;
        foreach (var stale in state.Users.SelectMany(u => u.Tokens))
            stale.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        File.WriteAllText(scratch.Path, JsonSerializer.Serialize(state));

        var store2 = new DomStore(scratch.Path, Quiet);
        var rejected = store2.Resolve(token) is null;

        // the rejection also drops it, so the file no longer carries a token nobody can use
        var pruned = !File.ReadAllText(scratch.Path).Contains(token);
        var stillSignsIn = store2.Login("kris", "correct horse battery").token is not null;

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

        var kris = store.Register("kris", "correct horse battery").user!;
        var stranger = store.Register("stranger", "correct horse battery").user!;

        List<TrackSnapshot> tracks =
        [
            new() { Id = "local://1", Name = "Ноќ", Artist = "Someone", Duration = "00:03:41" },
            new() { Id = "yt://abc", Name = "Another", Artist = "Someone else", Duration = "00:04:02" }
        ];

        var (made, error, _) = store.Create(kris, "Late shift", false, tracks);
        var unnamed = store.Create(kris, "   ", false, null);

        var mineOnly = store.Mine(kris).Count == 1 && store.Public().Count == 0;
        var hidden = store.Visible(made!.Id, stranger) is null && store.Visible(made.Id, kris) is not null;

        store.Update(kris, made.Id, null, true, null);
        var shared = store.Public().Count == 1 && store.Visible(made.Id, stranger) is not null;

        // a playlist is only ever changed or removed by whoever owns it
        var strangerPatch = store.Update(stranger, made.Id, "Mine now", null, null).error;
        var strangerDelete = store.Delete(stranger, made.Id).deleted;

        var duration = store.Visible(made.Id, kris)!.Duration == TimeSpan.FromSeconds(3 * 60 + 41 + 4 * 60 + 2);

        var reloaded = new DomStore(scratch.Path, Quiet);
        var survived = reloaded.Public().Count == 1 && reloaded.Visible(made.Id, null)!.Tracks.Count == 2;

        var gone = reloaded.Delete(kris, made.Id).deleted && reloaded.Mine(kris).Count == 0;

        var ok = error is null
                 && unnamed.error == "invalid_request"
                 && mineOnly && hidden && shared && duration && survived && gone
                 && strangerPatch == "not_found"
                 && !strangerDelete;

        Report(ok, "a playlist is private until it is not, and only its owner can change it",
            $"created={error ?? "ok"} unnamed={unnamed.error} mineOnly={mineOnly} hidden={hidden} " +
            $"shared={shared} duration={duration} reloaded={survived} deleted={gone} " +
            $"strangerPatch={strangerPatch} strangerDeleted={strangerDelete}");

        return ok;
    }

    private static void Report(bool ok, string claim, string detail) =>
        Console.WriteLine(ok ? $"OK: {claim}" : $"FAIL: {claim} — {detail}");

    /// <summary>A throwaway accounts file, so a self-check run never touches a real one.</summary>
    private sealed class ScratchFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dom-selfcheck-" + Guid.NewGuid().ToString("n") + ".json");

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
                File.Delete(Path + ".tmp");
            }
            catch (IOException)
            {
                // a leftover scratch file in the temp directory is not worth failing a check over
            }
        }
    }
}
