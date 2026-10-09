namespace Moliv;

/// <summary>
///     The rules a broken merge would quietly break: what the trust boundary refuses, how a second write of
///     a play merges into the first, and what each route leaves behind. Run in the Docker build, against a
///     temporary database, with <c>dotnet run --project Moliv -- --self-check</c>.
/// </summary>
internal static class SelfCheck
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Caller Anonymous = new(Guid.NewGuid().ToString(), null);
    private static readonly Caller Kris = new(Guid.NewGuid().ToString(), "kris-id");

    public static bool Run()
    {
        var ok = Validation() & Merging() & Anonymity() & Routes();
        Console.WriteLine(ok ? "selftest OK" : "selftest FAILED");
        return ok;
    }

    private static bool Validation()
    {
        bool Refused(PlayBody body) => body.ToRow("x", Anonymous, Now).error is not null;

        var good = Body();
        var (row, error) = good.ToRow("x", Anonymous, Now);

        return Check("a whole body is a row", error is null && row is { TrackId: "audio://a", Shuffled: false })
               & Check("a track id without a protocol is refused", Refused(good with { TrackId = "nope" }))
               & Check("a track id past 512 characters is refused",
                   Refused(good with { TrackId = "yt://" + new string('x', 508) }))
               & Check("an unknown reason is refused", Refused(good with { StartReason = "telepathy" }))
               & Check("an unknown end reason is refused", Refused(good with { EndReason = "boredom" }))
               & Check("an unknown source is refused", Refused(good with { SourceKind = "radio" }))
               & Check("an unknown platform is refused", Refused(good with { Platform = "fridge" }))
               & Check("a missing field is refused", Refused(good with { Shuffled = null }))
               & Check("a negative time heard is refused", Refused(good with { PlayedMs = -1 }))
               & Check("a session that is not a UUID is refused", Refused(good with { SessionId = "today" }))
               & Check("a start in the future is refused", Refused(good with { StartedUtc = Now.AddMinutes(10) }))
               & Check("a start past the outbox's horizon is refused",
                   Refused(good with { StartedUtc = Now.AddDays(-31) }))
               & Check("a start in another offset is stored at +00:00",
                   good.ToRow("x", Anonymous, Now).row!.StartedUtc.Offset == TimeSpan.Zero &&
                   (good with { StartedUtc = Now.ToOffset(TimeSpan.FromHours(3)) }).ToRow("x", Anonymous, Now).row!
                   .StartedUtc.Offset == TimeSpan.Zero)
               & Check("time heard past the duration is clamped, not refused",
                   (good with { PlayedMs = 999_999 }).ToRow("x", Anonymous, Now).row!.PlayedMs == 185_000)
               & Check("time heard with no known duration is kept",
                   (good with { DurationMs = 0, PlayedMs = 999_999 }).ToRow("x", Anonymous, Now).row!.PlayedMs ==
                   999_999);
    }

    private static bool Merging()
    {
        using var scratch = new Scratch();
        var plays = scratch.Plays();
        var id = Guid.NewGuid().ToString();

        var first = Row(id, Anonymous, played: 40_000) with { EndReason = null };
        plays.Upsert(first);
        plays.Upsert(first with { PlayedMs = 10_000, EndReason = "skipped" }); // a late, stale retry
        plays.Upsert(first with { PlayedMs = 90_000, EndReason = "finished", TrackId = "yt://other" });
        var signedIn = plays.Upsert(first with { UserId = "kris-id", DeviceId = Anonymous.DeviceId });
        var otherDevice = plays.Upsert(first with { DeviceId = Guid.NewGuid().ToString(), PlayedMs = 1 });

        var stored = plays.List(Kris with { DeviceId = Anonymous.DeviceId }, null, 10).SingleOrDefault();

        return Check("time heard keeps the larger value", stored?.PlayedMs == 90_000)
               & Check("the end reason keeps the first one given", stored?.EndReason == "skipped")
               & Check("everything else is fixed by the first write", stored?.TrackId == "audio://a")
               & Check("an anonymous play gains the account that writes it", signedIn && stored is not null)
               & Check("another device cannot touch the play", !otherDevice);
    }

    private static bool Anonymity()
    {
        using var scratch = new Scratch();
        var plays = scratch.Plays(anonymousLimit: 3);

        var stranger = new Caller(Guid.NewGuid().ToString(), null);
        plays.Upsert(Row(Guid.NewGuid().ToString(), stranger, Now.AddDays(-89)));
        for (var minute = 5; minute >= 0; minute--)
            plays.Upsert(Row(Guid.NewGuid().ToString(), Anonymous, Now.AddMinutes(-minute)));
        var kept = plays.List(Anonymous, null, 10);

        // what nobody claimed in 90 days goes the next time anybody writes anonymously
        plays.Upsert(Row(Guid.NewGuid().ToString(), Kris, Now.AddDays(-100)));
        plays.Upsert(Row(Guid.NewGuid().ToString(), stranger, Now.AddDays(-91)) with { UpdatedUtc = Now.AddDays(-2) });
        var beforeExpiry = plays.List(stranger, null, 10).Count;
        plays.Upsert(Row(Guid.NewGuid().ToString(), Anonymous, Now));
        var strangerLeft = plays.List(stranger, null, 10).Count;

        return Check("one device keeps only its newest anonymous plays",
                   kept.Count == 3 && kept[0].StartedUtc > kept[2].StartedUtc &&
                   kept.All(play => play.StartedUtc > Now.AddMinutes(-3)))
               & Check("anonymous plays older than 90 days expire, on any device",
                   beforeExpiry == 2 && strangerLeft == 1)
               & Check("an account's plays never expire", plays.List(Kris, null, 10).Count == 1);
    }

    private static bool Routes()
    {
        using var scratch = new Scratch();
        var plays = scratch.Plays();

        // two plays of one track, one of another, and a skip nobody wants to see
        var older = Row(Guid.NewGuid().ToString(), Anonymous, Now.AddMinutes(-30)) with { TrackId = "yt://song" };
        plays.Upsert(older);
        plays.Upsert(Row(Guid.NewGuid().ToString(), Anonymous, Now.AddMinutes(-20)));
        plays.Upsert(Row(Guid.NewGuid().ToString(), Anonymous, Now.AddMinutes(-10)) with { TrackId = "yt://song" });
        plays.Upsert(Row(Guid.NewGuid().ToString(), Anonymous, Now, played: 2_000) with { EndReason = "skipped" });
        plays.Upsert(Row(Guid.NewGuid().ToString(), Anonymous, Now.AddMinutes(-1), played: 2_000) with
        {
            TrackId = "deezer://short", DurationMs = 2_000, EndReason = "finished"
        });

        var first = plays.List(Anonymous, null, 2);
        var second = plays.List(Anonymous, Api.ParseCursor(Api.Cursor(first[^1])), 2);
        var recent = plays.Recent(Anonymous, 12);

        var claimed = plays.Claim("kris-id", Anonymous.DeviceId);
        var nowMine = plays.List(Kris, null, 10).Count;
        var leftAnonymous = plays.List(Anonymous, null, 10).Count;
        var snapshot = plays.Snapshot();

        var cleared = plays.Clear(Kris);
        plays.Upsert(Row(Guid.NewGuid().ToString(), Kris, Now));
        var forgotten = plays.Forget("kris-id");

        return Check("a short skip is kept but not shown; a short track heard to its end is",
                   first.Count == 2 && first[0].DurationMs == 2_000)
               & Check("the cursor picks up exactly where the page ended",
                   second.Count == 2 && second.All(play => first.All(seen => seen.Id != play.Id)) &&
                   second[^1].Id == older.Id)
               & Check("recent is distinct tracks, newest first",
                   recent.Count == 3 && recent[1].TrackId == "yt://song" &&
                   recent.Select(play => play.TrackId).Distinct().Count() == 3)
               & Check("claim moves every anonymous play of the device", claimed == 5 && nowMine == 4 && leftAnonymous == 0)
               & Check("the snapshot counts", snapshot.ToString()!.Contains("Plays = 5"))
               & Check("clear deletes the account's plays", cleared == 5)
               & Check("forget deletes the account's plays", forgotten == 1);
    }

    private static PlayBody Body() => new("audio://a", Now.AddMinutes(-3), 180, 180_000, 60_000, "chosen", null,
        "search", null, Guid.NewGuid().ToString(), 0, "web", "desktop", false);

    private static PlayRow Row(string id, Caller caller, DateTimeOffset? started = null, long played = 60_000) =>
        new(id, caller.UserId, caller.DeviceId, "audio://a", started ?? Now, 180, 180_000, played, "chosen", "finished",
            "search", null, Guid.NewGuid().ToString(), 0, "web", "desktop", false, Now);

    private static bool Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok;
    }

    /// <summary>A throwaway database, so a self-check run never touches a real one.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"moliv-selfcheck-{Guid.NewGuid():n}.db");

        public Plays Plays(int anonymousLimit = 1000) => new(_path, anonymousLimit);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in (string[])["", "-wal", "-shm"]) File.Delete(_path + suffix);
        }
    }
}
