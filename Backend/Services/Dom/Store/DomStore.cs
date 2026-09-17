using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILogger = Serilog.ILogger;

namespace Dom.Store;

/// <summary>
///     Every account Dom knows about, held in memory and written to one JSON file.
/// </summary>
/// <remarks>
///     This is the first state in the stack that has to survive a restart, so the write is atomic:
///     serialise beside the target, then <see cref="File.Move(string,string,bool)" /> over it. A
///     half-written accounts file is everybody locked out.
///     <para>
///         ponytail: one lock and a whole-file rewrite per mutation. Registers and logins are rare and
///         the serialise is sub-millisecond at any plausible size; split into per-user files, or move to
///         SQLite, when a write actually shows up in a trace.
///     </para>
/// </remarks>
public sealed class DomStore
{
    /// <summary>OWASP's floor for PBKDF2-SHA256. Stored per user, so raising it is not a migration.</summary>
    private const int DefaultIterations = 210_000;

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    private readonly Dictionary<string, User> _byToken = new(StringComparer.Ordinal);
    private readonly string _dataFile;
    private readonly Lock _gate = new();
    private readonly ILogger _log;
    private readonly Dictionary<string, Playlist> _playlists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, User> _users = new(StringComparer.Ordinal);

    public DomStore(string dataFile, ILogger log)
    {
        _dataFile = dataFile;
        _log = log;
        Load();
    }

    /// <summary>How long a fresh token lasts. Fixed, not sliding — see <see cref="Resolve" />.</summary>
    private static TimeSpan TokenLifetime => TimeSpan.FromDays(30);

    public int UserCount
    {
        get
        {
            lock (_gate) return _users.Count;
        }
    }

    /// <summary>
    ///     Creates an account and signs it in. Returns <c>username_taken</c> or an
    ///     <c>invalid_request</c> reason instead of a token when the input will not do.
    /// </summary>
    public (Token? token, User? user, string? error, string? message) Register(string username, string password)
    {
        var (error, message) = Validate(username, password);
        if (error is not null) return (null, null, error, message);

        var name = username.Trim();

        lock (_gate)
        {
            if (_users.ContainsKey(User.Normalize(name)))
                return (null, null, "username_taken", "That username is taken. Pick another.");

            var salt = RandomNumberGenerator.GetBytes(16);
            var user = new User
            {
                Username = name,
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(Derive(password, salt, DefaultIterations)),
                Iterations = DefaultIterations,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            _users[user.Key] = user;
            var token = IssueLocked(user);
            SaveLocked();

            _log.Information("Registered {Username}", user.Username);
            return (token, user, null, null);
        }
    }

    /// <summary>
    ///     Signs in an existing account. One error for both a missing user and a wrong password: which
    ///     of the two it was is exactly what an attacker enumerating usernames wants to know.
    /// </summary>
    public (Token? token, User? user, string? error, string? message) Login(string username, string password)
    {
        const string wrong = "Wrong username or password.";

        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username ?? ""), out var user))
                return (null, null, "invalid_credentials", wrong);

            var expected = Convert.FromBase64String(user.Hash);
            var actual = Derive(password ?? "", Convert.FromBase64String(user.Salt), user.Iterations);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                return (null, null, "invalid_credentials", wrong);

            var token = IssueLocked(user);
            SaveLocked();

            return (token, user, null, null);
        }
    }

    /// <summary>
    ///     The account a bearer token belongs to, or <c>null</c> if it is unknown or expired.
    /// </summary>
    /// <remarks>
    ///     Deliberately does not slide the expiry: sliding would mean a whole-file write on every
    ///     authenticated request, which is the one thing the storage above cannot afford. Thirty days,
    ///     then sign in again.
    /// </remarks>
    public User? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        lock (_gate)
        {
            if (!_byToken.TryGetValue(token, out var user)) return null;

            var live = user.Tokens.FirstOrDefault(t => t.Value == token);
            if (live is not null && live.ExpiresUtc > DateTimeOffset.UtcNow) return user;

            // expired: drop it here rather than waiting for the next login's prune
            user.Tokens.RemoveAll(t => t.Value == token);
            _byToken.Remove(token);
            SaveLocked();

            return null;
        }
    }

    /// <summary>Revokes one token. Signing out on one device leaves the others signed in.</summary>
    public void Logout(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;

        lock (_gate)
        {
            if (!_byToken.Remove(token, out var user)) return;

            user.Tokens.RemoveAll(t => t.Value == token);
            SaveLocked();
        }
    }

    /// <summary>Everything the account owns, newest first.</summary>
    public List<Playlist> Mine(User owner)
    {
        lock (_gate)
            return _playlists.Values
                .Where(p => p.OwnerKey == owner.Key)
                .OrderByDescending(p => p.UpdatedUtc)
                .ToList();
    }

    /// <summary>Everything anybody made public, newest first.</summary>
    public List<Playlist> Public()
    {
        lock (_gate)
            return _playlists.Values
                .Where(p => p.IsPublic)
                .OrderByDescending(p => p.UpdatedUtc)
                .ToList();
    }

    /// <summary>
    ///     One playlist, if <paramref name="viewer" /> may see it. A private playlist is
    ///     indistinguishable from one that never existed — a 403 would confirm it does.
    /// </summary>
    public Playlist? Visible(string id, User? viewer)
    {
        lock (_gate)
        {
            if (!_playlists.TryGetValue(id, out var playlist)) return null;

            return playlist.IsPublic || playlist.OwnerKey == viewer?.Key ? playlist : null;
        }
    }

    /// <summary>Creates a playlist for <paramref name="owner" />, or says what is wrong with it.</summary>
    public (Playlist? playlist, string? error, string? message) Create(
        User owner, string? name, bool isPublic, List<TrackSnapshot>? tracks)
    {
        var (error, message) = ValidatePlaylist(name, tracks);
        if (error is not null) return (null, error, message);

        var now = DateTimeOffset.UtcNow;
        var playlist = new Playlist
        {
            Id = "p_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            Owner = owner.Username,
            Name = name!.Trim(),
            IsPublic = isPublic,
            Tracks = Clean(tracks),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        lock (_gate)
        {
            _playlists[playlist.Id] = playlist;
            SaveLocked();
        }

        _log.Information("{Owner} created playlist {Name} ({Tracks} tracks)",
            owner.Username, playlist.Name, playlist.Tracks.Count);

        return (playlist, null, null);
    }

    /// <summary>
    ///     Changes whichever of name, visibility and tracks were sent. A field left <c>null</c> is a
    ///     field the caller did not mention, not a field being cleared.
    /// </summary>
    public (Playlist? playlist, string? error, string? message) Update(
        User owner, string id, string? name, bool? isPublic, List<TrackSnapshot>? tracks)
    {
        var (error, message) = ValidatePlaylist(name ?? "unchanged", tracks);
        if (error is not null) return (null, error, message);

        lock (_gate)
        {
            if (!_playlists.TryGetValue(id, out var playlist) || playlist.OwnerKey != owner.Key)
                return (null, "not_found", "No such playlist.");

            if (name is not null) playlist.Name = name.Trim();
            if (isPublic is not null) playlist.IsPublic = isPublic.Value;
            if (tracks is not null) playlist.Tracks = Clean(tracks);
            playlist.UpdatedUtc = DateTimeOffset.UtcNow;

            SaveLocked();

            return (playlist, null, null);
        }
    }

    /// <summary>Removes a playlist the caller owns. Returns the cover file to delete, if there was one.</summary>
    public (bool deleted, string? coverFile) Delete(User owner, string id)
    {
        lock (_gate)
        {
            if (!_playlists.TryGetValue(id, out var playlist) || playlist.OwnerKey != owner.Key)
                return (false, null);

            _playlists.Remove(id);
            SaveLocked();

            return (true, playlist.CoverFile);
        }
    }

    // ── Admin ──────────────────────────────────────────────────────────────────────────────────
    // Owner-agnostic on purpose: everything above asks "does this caller own it", and an operator
    // owns nothing. Every one of these destroys or rewrites real user data that no cache refills,
    // which is why Oko records each call before it makes it. See ADMIN_PLAN.md.

    /// <summary>Renames an account, carrying its playlists across with it.</summary>
    public (bool ok, string? error) AdminRenameUser(string username, string? newName)
    {
        var invalid = ValidateUsername(newName);
        if (invalid is not null) return (false, invalid);

        var name = newName!.Trim();

        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user)) return (false, "No such account.");

            var oldKey = user.Key;
            var newKey = User.Normalize(name);
            if (newKey != oldKey && _users.ContainsKey(newKey)) return (false, "That username is taken.");

            // Playlist.Owner holds the display name and OwnerKey derives from it, so the playlists
            // have to move with the account or every one of them orphans on rename.
            foreach (var playlist in _playlists.Values.Where(playlist => playlist.OwnerKey == oldKey))
                playlist.Owner = name;

            _users.Remove(oldKey);
            user.Username = name;
            _users[user.Key] = user;

            SaveLocked();
            return (true, null);
        }
    }

    /// <summary>
    ///     Sets a new password and signs every session out. The sign-out is not optional: each live
    ///     token was issued against the old password, so leaving them alone locks nobody out.
    /// </summary>
    public (bool ok, string? error) AdminSetPassword(string username, string? password)
    {
        var invalid = ValidatePassword(password);
        if (invalid is not null) return (false, invalid);

        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user)) return (false, "No such account.");

            var salt = RandomNumberGenerator.GetBytes(16);
            user.Salt = Convert.ToBase64String(salt);
            user.Hash = Convert.ToBase64String(Derive(password!, salt, DefaultIterations));
            user.Iterations = DefaultIterations;

            RevokeLocked(user);

            SaveLocked();
            return (true, null);
        }
    }

    /// <summary>Revokes every token an account holds. Returns how many sessions ended.</summary>
    public (bool ok, int revoked) AdminSignOut(string username)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user)) return (false, 0);

            var revoked = user.Tokens.Count;
            RevokeLocked(user);

            SaveLocked();
            return (true, revoked);
        }
    }

    /// <summary>
    ///     Deletes an account and everything it owns. Returns the cover files left behind, which are
    ///     the caller's to unlink — the store owns the accounts file and nothing else on disk.
    /// </summary>
    public (bool ok, List<string> covers, int _playlists) AdminDeleteUser(string username)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user)) return (false, [], 0);

            var owned = _playlists.Values.Where(playlist => playlist.OwnerKey == user.Key).ToList();
            foreach (var playlist in owned) _playlists.Remove(playlist.Id);

            RevokeLocked(user);
            _users.Remove(user.Key);

            SaveLocked();
            return (true, owned.Where(p => p.CoverFile is not null).Select(p => p.CoverFile!).ToList(), owned.Count);
        }
    }

    /// <summary>
    ///     Changes whichever of name, visibility and one track position were given. Mirrors
    ///     <see cref="Update" /> without the ownership check.
    /// </summary>
    public (bool ok, string? error) AdminUpdatePlaylist(string id, string? name, bool? isPublic, int? removeTrack)
    {
        lock (_gate)
        {
            if (!_playlists.TryGetValue(id, out var playlist)) return (false, "No such playlist.");

            if (name is not null)
            {
                var (error, message) = ValidatePlaylist(name, null);
                if (error is not null) return (false, message);
                playlist.Name = name.Trim();
            }

            if (isPublic is not null) playlist.IsPublic = isPublic.Value;

            if (removeTrack is { } index)
            {
                if (index < 0 || index >= playlist.Tracks.Count) return (false, "No track at that position.");
                playlist.Tracks.RemoveAt(index);
            }

            playlist.UpdatedUtc = DateTimeOffset.UtcNow;

            SaveLocked();
            return (true, null);
        }
    }

    /// <summary>Deletes any playlist. Returns its cover file for the caller to unlink.</summary>
    public (bool ok, string? cover) AdminDeletePlaylist(string id)
    {
        lock (_gate)
        {
            if (!_playlists.Remove(id, out var playlist)) return (false, null);

            SaveLocked();
            return (true, playlist.CoverFile);
        }
    }

    /// <summary>Drops every token an account holds, from the account and from the lookup.</summary>
    private void RevokeLocked(User user)
    {
        foreach (var token in user.Tokens) _byToken.Remove(token.Value);
        user.Tokens.Clear();
    }

    /// <summary>
    ///     The operator's view of every account and playlist. Salt, hash and token values are never in
    ///     here: an admin panel needs to know an account exists and how many live sessions it has, and
    ///     nothing on this endpoint should be worth stealing.
    /// </summary>
    public object Snapshot()
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var counts = _playlists.Values.GroupBy(playlist => playlist.OwnerKey)
                .ToDictionary(group => group.Key, group => group.Count());

            return new
            {
                _users = _users.Values.Select(user => new
                {
                    username = user.Username,
                    createdUtc = user.CreatedUtc,
                    activeTokens = user.Tokens.Count(token => token.ExpiresUtc > now),
                    _playlists = counts.GetValueOrDefault(user.Key, 0)
                }).OrderBy(user => user.username).ToList(),
                _playlists = _playlists.Values.Select(playlist => new
                {
                    id = playlist.Id,
                    name = playlist.Name,
                    owner = playlist.Owner,
                    isPublic = playlist.IsPublic,
                    tracks = playlist.Tracks.Count,
                    duration = playlist.Duration.ToString(),
                    hasCover = playlist.CoverFile is not null,
                    createdUtc = playlist.CreatedUtc,
                    updatedUtc = playlist.UpdatedUtc
                }).OrderBy(playlist => playlist.owner).ToList()
            };
        }
    }

    /// <summary>Points a playlist at an uploaded cover. The file itself is the controller's business.</summary>
    public void SetCover(User owner, string id, string? coverFile)
    {
        lock (_gate)
        {
            if (!_playlists.TryGetValue(id, out var playlist) || playlist.OwnerKey != owner.Key) return;

            playlist.CoverFile = coverFile;
            playlist.UpdatedUtc = DateTimeOffset.UtcNow;
            SaveLocked();
        }
    }

    /// <summary>
    ///     What a playlist has to be. The track cap is not a business rule, it is the whole-file
    ///     rewrite above: a playlist nobody can serialise quickly is a service nobody can log in to.
    /// </summary>
    private static (string? error, string? message) ValidatePlaylist(string? name, List<TrackSnapshot>? tracks)
    {
        var trimmed = name?.Trim() ?? "";

        if (trimmed.Length is < 1 or > 80)
            return ("invalid_request", "A playlist name is between 1 and 80 characters.");
        if (trimmed.Any(char.IsControl))
            return ("invalid_request", "A playlist name cannot contain control characters.");
        if (tracks is { Count: > 1000 })
            return ("invalid_request", "A playlist holds at most 1000 tracks.");
        if (tracks is not null && tracks.Any(t => string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name)))
            return ("invalid_request", "Every track needs an id and a name.");

        return (null, null);
    }

    /// <summary>Trims what came off the wire down to the fields a row draws, and nothing else.</summary>
    private static List<TrackSnapshot> Clean(List<TrackSnapshot>? tracks) =>
    [
        .. (tracks ?? []).Select(t => new TrackSnapshot
        {
            Id = t.Id.Trim(),
            Name = t.Name.Trim(),
            Artist = (t.Artist ?? "").Trim(),
            Album = string.IsNullOrWhiteSpace(t.Album) ? null : t.Album.Trim(),
            Duration = TimeSpan.TryParse(t.Duration, out var length) ? length.ToString("c") : "00:00:00",
            ThumbnailUrl = string.IsNullOrWhiteSpace(t.ThumbnailUrl) ? null : t.ThumbnailUrl.Trim()
        })
    ];

    /// <summary>
    ///     What a username and password have to be. Deliberately permissive about script — the library
    ///     this fronts is largely Cyrillic-tagged and a Latin-only rule would be the wrong kind of tidy —
    ///     and strict about the things that actually cause trouble: whitespace, control characters, and
    ///     an unbounded password, which is an unbounded PBKDF2.
    /// </summary>
    private static (string? error, string? message) Validate(string? username, string? password)
    {
        var name = ValidateUsername(username);
        if (name is not null) return ("invalid_request", name);

        var secret = ValidatePassword(password);
        return secret is not null ? ("invalid_request", secret) : (null, null);
    }

    /// <summary>The username half, on its own, because an admin rename changes one without the other.</summary>
    private static string? ValidateUsername(string? username)
    {
        var name = username?.Trim() ?? "";

        if (name.Length is < 2 or > 32) return "A username is between 2 and 32 characters.";
        return name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? "A username cannot contain spaces."
            : null;
    }

    private static string? ValidatePassword(string? password)
    {
        if ((password ?? "").Length < 8) return "A password is at least 8 characters.";
        return password!.Length > 256 ? "A password is at most 256 characters." : null;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private Token IssueLocked(User user)
    {
        var now = DateTimeOffset.UtcNow;

        // whoever just signed in is the natural moment to sweep their dead tokens
        foreach (var dead in user.Tokens.Where(t => t.ExpiresUtc <= now).ToList())
        {
            _byToken.Remove(dead.Value);
            user.Tokens.Remove(dead);
        }

        var token = new Token
        {
            Value = Base64Url(RandomNumberGenerator.GetBytes(32)),
            IssuedUtc = now,
            ExpiresUtc = now + TokenLifetime
        };

        user.Tokens.Add(token);
        _byToken[token.Value] = user;

        return token;
    }

    /// <summary>A token travels in a header and in <c>localStorage</c>; keep it URL- and copy-safe.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Load()
    {
        if (!File.Exists(_dataFile))
        {
            _log.Information("No accounts file at {Path} yet; starting empty", _dataFile);
            return;
        }

        var state = JsonSerializer.Deserialize<DomState>(File.ReadAllText(_dataFile), FileJson)
                    ?? new DomState();

        lock (_gate)
        {
            foreach (var user in state.Users)
            {
                _users[user.Key] = user;
                foreach (var token in user.Tokens) _byToken[token.Value] = user;
            }

            foreach (var playlist in state.Playlists) _playlists[playlist.Id] = playlist;
        }

        _log.Information("Loaded {Users} account(s) and {Playlists} playlist(s) from {Path}",
            state.Users.Count, state.Playlists.Count, _dataFile);
    }

    /// <summary>Caller holds <see cref="_gate" />.</summary>
    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(_dataFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Same directory as the target, so the move is a rename within one filesystem and therefore
        // atomic. A temp file in /tmp would be a copy, which is exactly the torn write to avoid.
        var temporary = _dataFile + ".tmp";
        var state = new DomState { Users = [.. _users.Values], Playlists = [.. _playlists.Values] };
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, FileJson));
        File.Move(temporary, _dataFile, true);
    }
}
