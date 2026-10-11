using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ILogger = Serilog.ILogger;

namespace Dom.Store;

/// <summary>
///     Every account Dom knows about, held in memory and written through to <c>dom.db</c>.
/// </summary>
/// <remarks>
///     Memory is the authority while the process runs and the database is what it reloads from — see
///     <see cref="DomDatabase" />.
///     <para>
///         ponytail: one lock for the whole store, and a playlist's tracks rewritten whole when any of them
///         changes. Split the lock, or write one track at a time, when a write shows up in a trace.
///     </para>
/// </remarks>
public sealed class DomStore
{
    /// <summary>What a password-gated change says when the password is wrong.</summary>
    private static readonly Refusal WrongPassword = new(Failure.InvalidCredentials, "That password is wrong.");

    /// <summary>What every route that needs an account says to a caller without one.</summary>
    public static readonly Refusal SignInFirst = new(Failure.Unauthorized, "Sign in first.");

    private static readonly Refusal UsernameTaken = new(Failure.UsernameTaken, "That username is taken. Pick another.");

    private readonly Dictionary<string, User> _byToken = new(StringComparer.Ordinal);
    private readonly DomDatabase _database;
    private readonly Lock _gate = new();

    /// <summary>
    ///     Live friend codes, by code. Memory only: a code lasts fifteen minutes, so a restart voiding
    ///     the live ones costs less than a file write per code.
    /// </summary>
    private readonly Dictionary<string, Invite> _invites = new(StringComparer.Ordinal);

    private readonly ILogger _log;
    private readonly Dictionary<string, Playlist> _playlists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, User> _users = new(StringComparer.Ordinal);

    /// <param name="dataFile">
    ///     Where the database lives. <c>Dom:DataFile</c> named the JSON file before, so whatever extension
    ///     it carries, the database is the <c>.db</c> beside it and the JSON is the legacy import.
    /// </param>
    /// <param name="log">Where the store reports what it did.</param>
    public DomStore(string dataFile, ILogger log)
    {
        _database = new DomDatabase(Path.ChangeExtension(dataFile, ".db"), log);
        _log = log;

        var (users, playlists) = _database.Load(Path.ChangeExtension(dataFile, ".json"));
        lock (_gate)
        {
            foreach (var user in users)
            {
                _users[user.Key] = user;
                foreach (var token in user.Tokens) _byToken[token.Value] = user;
            }

            foreach (var playlist in playlists) _playlists[playlist.Id] = playlist;
        }
    }

    /// <summary>How long a token lasts from its last use — it slides, see <see cref="Resolve" />.</summary>
    private static TimeSpan TokenLifetime => TimeSpan.FromDays(30);

    /// <summary>
    ///     How much a slide has to be worth before it is written down. Every write is a transaction under
    ///     the one lock, so sliding on every authenticated request would put a disk sync in front of every
    ///     one of them; a day's granularity makes it one write per active token per day and costs a token
    ///     at most a day of the thirty.
    /// </summary>
    private static TimeSpan SlideGranularity => TimeSpan.FromDays(1);

    /// <summary>How long a friend code works, for everybody who uses it.</summary>
    private static TimeSpan InviteLifetime => TimeSpan.FromMinutes(15);

    /// <summary>Crockford's base32: no I, L, O or U, so a code read out loud cannot be misheard as another.</summary>
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

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
    public Outcome<Session> Register(string username, string password)
    {
        if (!IsValidUsername(username, out var message) || !IsValidPassword(password, out message))
            return new Refusal(Failure.InvalidRequest, message);

        var name = username.Trim();

        lock (_gate)
        {
            if (_users.ContainsKey(User.Normalize(name))) return UsernameTaken;

            var (salt, hash, iterations) = Passwords.Hash(password);
            var user = new User
            {
                Username = name,
                Salt = salt,
                Hash = hash,
                Iterations = iterations,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            _users[user.Key] = user;
            var token = IssueLocked(user);
            SaveLocked(users: [user]);

            _log.Information("Registered {Username}", user.Username);
            return new Session(user, token);
        }
    }

    /// <summary>
    ///     Signs in an existing account. One error for both a missing user and a wrong password: which
    ///     of the two it was is exactly what an attacker enumerating usernames wants to know.
    /// </summary>
    public Outcome<Session> Login(string username, string password)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user) || !Passwords.Verify(user, password))
                return new Refusal(Failure.InvalidCredentials, "Wrong username or password.");

            var token = IssueLocked(user);
            SaveLocked(users: [user]);

            return new Session(user, token);
        }
    }

    /// <summary>
    ///     The account a bearer token belongs to, or <c>null</c> if it is unknown or expired.
    /// </summary>
    /// <remarks>
    ///     The expiry slides: thirty days from last use, not from sign-in, so somebody who keeps using
    ///     the app is never signed out mid-session. The write that records it is throttled to
    ///     <see cref="SlideGranularity" /> — see the note there for why.
    ///     <para>
    ///         The client keeps its own copy of the expiry and signs itself out when it passes, so the
    ///         slid value has to be readable: <c>/Audio/Accounts/Me</c> returns it.
    ///     </para>
    /// </remarks>
    public User? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        lock (_gate)
        {
            if (!_byToken.TryGetValue(token, out var user)) return null;

            var live = user.Tokens.FirstOrDefault(t => t.Value == token);
            if (live is not null && live.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                var slid = DateTimeOffset.UtcNow + TokenLifetime;
                if (slid - live.ExpiresUtc >= SlideGranularity)
                {
                    live.ExpiresUtc = slid;
                    SaveLocked(users: [user]);
                }

                return user;
            }

            // expired: drop it here rather than waiting for the next login's prune
            user.Tokens.RemoveAll(t => t.Value == token);
            _byToken.Remove(token);
            SaveLocked(users: [user]);

            return null;
        }
    }

    /// <summary>When a token stops working, or <c>null</c> if it is not one. Read it after
    ///     <see cref="Resolve" />, which is what may have just moved it.</summary>
    public DateTimeOffset? ExpiryOf(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        lock (_gate)
        {
            return _byToken.TryGetValue(token, out var user)
                ? user.Tokens.FirstOrDefault(t => t.Value == token)?.ExpiresUtc
                : null;
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
            SaveLocked(users: [user]);
        }
    }

    /// <summary>Everything the account can edit — what it owns and what it collaborates on — newest first.</summary>
    public List<Playlist> Mine(User owner)
    {
        lock (_gate)
            return [.. _playlists.Values
                .Where(p => p.OwnerKey == owner.Key || Collaborates(p, owner))
                .OrderByDescending(p => p.UpdatedUtc)];
    }

    /// <summary>Everything anybody made public, newest first.</summary>
    public List<Playlist> Public()
    {
        lock (_gate)
            return [.. _playlists.Values
                .Where(p => p.Visibility == Visibility.Public)
                .OrderByDescending(p => p.UpdatedUtc)];
    }

    /// <summary>
    ///     Friends-only playlists of the account's friends, newest first. Their public ones are
    ///     already in <see cref="Public" />, and the ones the account edits in <see cref="Mine" />,
    ///     so neither is repeated here.
    /// </summary>
    public List<Playlist> FriendsPlaylists(User user)
    {
        lock (_gate)
            return [.. _playlists.Values
                .Where(p => p.Visibility == Visibility.Friends && p.OwnerKey != user.Key
                            && IsFriend(user, p.Owner) && !Collaborates(p, user))
                .OrderByDescending(p => p.UpdatedUtc)];
    }

    /// <summary>
    ///     One playlist, if <paramref name="viewer" /> may see it. A playlist you may not see is
    ///     indistinguishable from one that never existed — a 403 would confirm it does.
    /// </summary>
    public Playlist? Visible(string id, User? viewer)
    {
        lock (_gate)
            return _playlists.TryGetValue(id, out var playlist) && CanSeeLocked(playlist, viewer) ? playlist : null;
    }

    /// <summary>Creates a playlist for <paramref name="owner" />, or says what is wrong with it.</summary>
    public Outcome<Playlist> Create(User owner, string? name, Visibility visibility, List<TrackSnapshot>? tracks)
    {
        if (!IsValidPlaylist(name, tracks, out var message)) return new Refusal(Failure.InvalidRequest, message);

        var now = DateTimeOffset.UtcNow;
        var playlist = new Playlist
        {
            Id = "p_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            Owner = owner.Username,
            Name = name.Trim(),
            Visibility = visibility,
            Tracks = Clean(tracks),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        lock (_gate)
        {
            _playlists[playlist.Id] = playlist;
            SaveLocked(playlists: [playlist]);
        }

        _log.Information("{Owner} created playlist {Name} ({Tracks} tracks)",
            owner.Username, playlist.Name, playlist.Tracks.Count);

        return playlist;
    }

    /// <summary>
    ///     Changes whichever of name, visibility, collaborators and tracks were sent. A field left
    ///     <c>null</c> is a field the caller did not mention, not a field being cleared.
    /// </summary>
    /// <remarks>
    ///     The owner may change all of it; a collaborator only the tracks. Tracks replace the whole
    ///     list, so they come with the <paramref name="revision" /> the caller last saw, and a list
    ///     that has moved on since is refused rather than overwritten — with collaborators, the
    ///     overwrite would silently delete somebody else's additions.
    /// </remarks>
    public Outcome<Playlist> Update(User caller, string id, string? name, Visibility? visibility,
        List<TrackSnapshot>? tracks, List<string>? collaborators = null, int? revision = null)
    {
        if (!IsValidPlaylist(name ?? "unchanged", tracks, out var message))
            return new Refusal(Failure.InvalidRequest, message);

        lock (_gate)
        {
            if (!TryEditableLocked(id, caller, out var playlist, out var owns, out var refusal)) return refusal;

            if (!owns && (name is not null || visibility is not null || collaborators is not null))
                return new Refusal(Failure.Forbidden, "Only the owner can change that.");

            List<string>? team = null;
            if (collaborators is not null && !TryTeam(caller, collaborators, out team, out refusal)) return refusal;

            if (tracks is not null && revision != playlist.Revision)
                return new Refusal(Failure.Stale, "Someone else changed this playlist. This is the latest.");

            if (name is not null) playlist.Name = name.Trim();
            if (visibility is not null) playlist.Visibility = visibility.Value;
            if (team is not null) playlist.Collaborators = team;
            if (tracks is not null)
            {
                playlist.Tracks = Attribute(playlist.Tracks, Clean(tracks), owns ? null : caller.Username);
                playlist.Revision++;
            }

            playlist.UpdatedUtc = DateTimeOffset.UtcNow;

            SaveLocked(playlists: [playlist]);

            return playlist;
        }
    }

    /// <summary>
    ///     The collaborators asked for, as the caller's friends spell themselves, each once. Refused at the first
    ///     name that is not a friend.
    /// </summary>
    private static bool TryTeam(User caller, List<string> collaborators, [NotNullWhen(true)] out List<string>? team,
        [NotNullWhen(false)] out Refusal? refusal)
    {
        team = [];
        refusal = null;

        foreach (var wanted in collaborators.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var friend = caller.Friends.FirstOrDefault(f => Same(f, wanted));
            if (friend is null)
            {
                team = null;
                refusal = new Refusal(Failure.NotAFriend, $"{wanted.Trim()} isn’t your friend.");
                return false;
            }

            if (!team.Any(t => Same(t, friend))) team.Add(friend);
        }

        return true;
    }

    /// <summary>
    ///     Puts one track at the end of a playlist the caller can edit, unless it is already in it —
    ///     <c>added</c> says which. Read and written under one lock, so an add from a track's menu
    ///     cannot undo an edit made somewhere else between a read and a <see cref="Update" />.
    /// </summary>
    public Outcome<Appended> Append(User owner, string id, TrackSnapshot? track)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.Name))
            return new Refusal(Failure.InvalidRequest, "Every track needs an id and a name.");

        var clean = Clean([track])[0];

        lock (_gate)
        {
            if (!TryEditableLocked(id, owner, out var playlist, out var owns, out var refusal)) return refusal;
            if (playlist.Tracks.Any(t => t.Id == clean.Id)) return new Appended(playlist, false);

            clean.AddedBy = owns ? null : owner.Username;

            // a new list, not an Add: a response being written outside the lock may be walking the old one
            playlist.Tracks = [.. playlist.Tracks, clean];
            playlist.Revision++;
            playlist.UpdatedUtc = DateTimeOffset.UtcNow;

            SaveLocked(playlists: [playlist]);

            return new Appended(playlist, true);
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
            SaveLocked(gonePlaylists: [id]);

            return (true, playlist.CoverFile);
        }
    }

    // ── Friends ────────────────────────────────────────────────────────────────────────────────
    // A friendship comes from a code the inviter shows. The code is for a group, not a person: it
    // works for everybody who uses it in its fifteen minutes, and using it does not use it up.

    /// <summary>The account's friends, alphabetically.</summary>
    public List<string> Friends(User user)
    {
        lock (_gate) return [.. user.Friends.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    ///     The account's live code, or a new one if it has none. Returning the live one is the point:
    ///     pressing "Show my code" again, or on a second device, must not void the code a group is
    ///     already scanning. <c>null</c> if the account was deleted meanwhile.
    /// </summary>
    public Invite? OpenInvite(User user)
    {
        lock (_gate)
        {
            if (!LiveLocked(user)) return null;
            if (LiveInviteLocked(user) is { } live) return live;

            // 40 bits: even with a thousand codes live, finding one takes about 10⁹ guesses
            string code;
            do code = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, 8));
            while (_invites.ContainsKey(code));

            var invite = new Invite { Code = code, Owner = user, ExpiresUtc = DateTimeOffset.UtcNow + InviteLifetime };
            _invites[code] = invite;

            return invite;
        }
    }

    /// <summary>The account's live code, or <c>null</c>. What the inviter's screen polls; it never makes one.</summary>
    public Invite? CurrentInvite(User user)
    {
        lock (_gate) return LiveInviteLocked(user);
    }

    /// <summary>Stops the account's code before its time. Friends it already made stay friends.</summary>
    public bool EndInvite(User user)
    {
        lock (_gate)
        {
            var live = LiveInviteLocked(user);
            return live is not null && _invites.Remove(live.Code);
        }
    }

    /// <summary>Who made a code and when it stops, for a visitor deciding whether to accept — signed in or not.</summary>
    public (string username, DateTimeOffset expiresUtc)? PeekInvite(string? code)
    {
        lock (_gate)
            return FindInviteLocked(code) is { } invite ? (invite.Owner.Username, invite.ExpiresUtc) : null;
    }

    /// <summary>
    ///     Makes the caller and the code's owner friends, both sides under one lock. The code stays
    ///     live for the next person in the group.
    /// </summary>
    public Outcome<Friendship> AcceptInvite(User caller, string? code)
    {
        lock (_gate)
        {
            if (!LiveLocked(caller)) return SignInFirst;
            if (FindInviteLocked(code) is not { } invite)
                return new Refusal(Failure.InvalidCode, "That code ran out or was ended. Ask for a new one.");

            var inviter = invite.Owner;
            if (ReferenceEquals(inviter, caller))
                return new Refusal(Failure.OwnCode, "That’s your own code. Show it to the people you want to add.");
            if (IsFriend(caller, inviter.Username)) return new Friendship(inviter.Username, true);

            caller.Friends = [.. caller.Friends, inviter.Username];
            inviter.Friends = [.. inviter.Friends, caller.Username];
            invite.Joined = [.. invite.Joined, caller.Username];

            SaveLocked(users: [caller, inviter]);
            _log.Information("{Caller} and {Inviter} are friends", caller.Username, inviter.Username);

            return new Friendship(inviter.Username, false);
        }
    }

    /// <summary>
    ///     Ends a friendship on both sides, and with it each one's place on the other's playlists.
    ///     The tracks either of them added stay, with their names on them.
    /// </summary>
    public bool Unfriend(User user, string? username)
    {
        lock (_gate)
        {
            var friend = user.Friends.FirstOrDefault(f => Same(f, username ?? ""));
            if (friend is null) return false;

            user.Friends = [.. user.Friends.Where(f => !Same(f, friend))];
            List<User> changed = [user];
            var dropped = DropCollaboratorLocked(user, friend);

            if (_users.TryGetValue(User.Normalize(friend), out var other))
            {
                other.Friends = [.. other.Friends.Where(f => !Same(f, user.Username))];
                changed.Add(other);
                dropped.AddRange(DropCollaboratorLocked(other, user.Username));
            }

            SaveLocked(users: changed, playlists: dropped);
            return true;
        }
    }

    // ── Self-service ───────────────────────────────────────────────────────────────────────────
    // The account holder's own versions of the admin actions below. Everything that could lock the
    // owner out asks for the password as well as the token: a stolen token alone is not enough.

    /// <summary>The account's settings and when they last changed. Both <c>null</c> if it never saved any.</summary>
    public (JsonObject? settings, DateTimeOffset? updatedUtc) Settings(User user)
    {
        // a copy, because the response is serialised outside the lock while a merge may be writing
        lock (_gate) return (user.Settings?.DeepClone().AsObject(), user.SettingsUpdatedUtc);
    }

    /// <summary>
    ///     Merges <paramref name="patch" /> into the account's settings, key by key at the top level; a
    ///     <c>null</c> value removes that key. A merge rather than a replace, so a tab left open since
    ///     morning overwrites only what it changed, not everything another device changed since.
    /// </summary>
    public (JsonObject settings, DateTimeOffset updatedUtc) MergeSettings(User user, JsonObject patch)
    {
        lock (_gate)
        {
            var settings = user.Settings ??= [];

            foreach (var (key, value) in patch)
            {
                if (value is null) settings.Remove(key);
                else settings[key] = value.DeepClone();
            }

            var now = DateTimeOffset.UtcNow;
            user.SettingsUpdatedUtc = now;
            SaveLocked(users: [user]);

            return (settings.DeepClone().AsObject(), now);
        }
    }

    /// <summary>
    ///     Sets a new password, signs every session out, and signs this one back in. The old tokens were
    ///     issued against the old password, so they all go — including the caller's, which is why it
    ///     gets a fresh one under the same lock.
    /// </summary>
    public Outcome<Token> ChangePassword(User user, string? current, string? password)
    {
        if (!IsValidPassword(password, out var invalid)) return new Refusal(Failure.InvalidRequest, invalid);

        lock (_gate)
        {
            if (!LiveLocked(user)) return SignInFirst;
            if (!Passwords.Verify(user, current)) return WrongPassword;

            Passwords.Set(user, password);
            RevokeLocked(user);
            var token = IssueLocked(user);

            SaveLocked(users: [user]);
            return token;
        }
    }

    /// <summary>Revokes every token but <paramref name="keep" />. Returns how many live sessions ended.</summary>
    public int SignOutEverywhere(User user, string keep)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var others = user.Tokens.Where(token => token.Value != keep).ToList();
            if (others.Count == 0) return 0;

            foreach (var token in others)
            {
                _byToken.Remove(token.Value);
                user.Tokens.Remove(token);
            }

            SaveLocked(users: [user]);

            // an expired token is not a device anybody is signed in on
            return others.Count(token => token.ExpiresUtc > now);
        }
    }

    /// <returns>Why the rename was refused, or <c>null</c> when it happened.</returns>
    public Refusal? Rename(User user, string? password, string? newName)
    {
        lock (_gate)
        {
            if (!LiveLocked(user)) return SignInFirst;
            return Passwords.Verify(user, password) ? RenameLocked(user, newName) : WrongPassword;
        }
    }

    /// <summary>Deletes the account and everything it owns. Returns the cover files for the caller to unlink.</summary>
    public Outcome<List<string>> DeleteAccount(User user, string? password)
    {
        lock (_gate)
        {
            if (!LiveLocked(user)) return SignInFirst;
            if (!Passwords.Verify(user, password)) return WrongPassword;

            var (covers, _) = DeleteLocked(user);
            _log.Information("{Username} deleted their account", user.Username);

            return covers;
        }
    }

    // ── Admin ──────────────────────────────────────────────────────────────────────────────────
    // Owner-agnostic on purpose: everything above asks "does this caller own it", and an operator
    // owns nothing. Every one of these destroys or rewrites real user data that no cache refills,
    // which is why Oko records each call before it makes it. See ADMIN_PLAN.md.

    /// <summary>Renames an account, carrying its playlists across with it.</summary>
    public (bool ok, string? error) AdminRenameUser(string username, string? newName)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user)) return (false, "No such account.");

            var refusal = RenameLocked(user, newName);
            return (refusal is null, refusal?.Message);
        }
    }

    /// <summary>
    ///     Sets a new password and signs every session out. The sign-out is not optional: each live
    ///     token was issued against the old password, so leaving them alone locks nobody out.
    /// </summary>
    public (bool ok, string? error) AdminSetPassword(string username, string? password)
    {
        if (!IsValidPassword(password, out var invalid)) return (false, invalid);

        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user)) return (false, "No such account.");

            Passwords.Set(user, password);
            RevokeLocked(user);

            SaveLocked(users: [user]);
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

            SaveLocked(users: [user]);
            return (true, revoked);
        }
    }

    /// <summary>
    ///     Deletes an account and everything it owns. Returns the cover files left behind, which are
    ///     the caller's to unlink — the store owns the accounts file and nothing else on disk.
    /// </summary>
    /// <returns>What went, or <c>null</c> when there is no such account.</returns>
    public DeletedAccount? AdminDeleteUser(string username)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(User.Normalize(username), out var user)) return null;

            var (covers, deleted) = DeleteLocked(user);
            return new DeletedAccount(user.Id, covers, deleted);
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
                if (!IsValidPlaylist(name, null, out var message)) return (false, message);
                playlist.Name = name.Trim();
            }

            // the operator's switch is still public or not; "not" is private, not friends
            if (isPublic is not null) playlist.Visibility = isPublic.Value ? Visibility.Public : Visibility.Private;

            if (removeTrack is { } index)
            {
                if (index < 0 || index >= playlist.Tracks.Count) return (false, "No track at that position.");
                playlist.Tracks = [.. playlist.Tracks.Where((_, at) => at != index)];
                playlist.Revision++;
            }

            playlist.UpdatedUtc = DateTimeOffset.UtcNow;

            SaveLocked(playlists: [playlist]);
            return (true, null);
        }
    }

    /// <summary>Deletes any playlist. Returns its cover file for the caller to unlink.</summary>
    public (bool ok, string? cover) AdminDeletePlaylist(string id)
    {
        lock (_gate)
        {
            if (!_playlists.Remove(id, out var playlist)) return (false, null);

            SaveLocked(gonePlaylists: [id]);
            return (true, playlist.CoverFile);
        }
    }

    /// <summary>
    ///     Whether <paramref name="user" /> is still an account. A controller resolves the caller before
    ///     it calls in, so a delete can land in between — and renaming or re-issuing a token for the
    ///     object left behind would bring the deleted account back. Caller holds <see cref="_gate" />.
    /// </summary>
    private bool LiveLocked(User user) => _users.TryGetValue(user.Key, out var live) && ReferenceEquals(live, user);

    private static readonly Refusal CannotEdit =
        new(Failure.Forbidden, "Only its owner and the friends they share it with can change this playlist.");

    private static readonly Refusal NoSuchPlaylist = new(Failure.NotFound, "No such playlist.");

    private static bool Same(string a, string b) => User.Normalize(a) == User.Normalize(b);

    private static bool IsFriend(User user, string username) => user.Friends.Any(f => Same(f, username));

    private static bool Collaborates(Playlist playlist, User user) =>
        playlist.Collaborators.Any(c => User.Normalize(c) == user.Key);

    /// <summary>
    ///     Public for everybody; otherwise the owner and the collaborators, and for a friends-only
    ///     playlist the owner's friends too. Caller holds <see cref="_gate" />.
    /// </summary>
    private bool CanSeeLocked(Playlist playlist, User? viewer)
    {
        if (playlist.Visibility == Visibility.Public) return true;
        if (viewer is null) return false;
        if (playlist.OwnerKey == viewer.Key || Collaborates(playlist, viewer)) return true;

        return playlist.Visibility == Visibility.Friends && IsFriend(viewer, playlist.Owner);
    }

    /// <summary>
    ///     The playlist, if the caller may change its tracks. Not found when they cannot see it, forbidden when
    ///     they can but it is not theirs to edit. Caller holds <see cref="_gate" />.
    /// </summary>
    private bool TryEditableLocked(string id, User caller, [NotNullWhen(true)] out Playlist? playlist,
        out bool owns, [NotNullWhen(false)] out Refusal? refusal)
    {
        owns = false;
        refusal = null;

        if (!_playlists.TryGetValue(id, out playlist) || !CanSeeLocked(playlist, caller))
        {
            playlist = null;
            refusal = NoSuchPlaylist;
            return false;
        }

        owns = playlist.OwnerKey == caller.Key;
        if (owns || Collaborates(playlist, caller)) return true;

        playlist = null;
        refusal = CannotEdit;
        return false;
    }

    /// <summary>
    ///     Carries each track's <see cref="TrackSnapshot.AddedBy" /> across a save that replaces the
    ///     list: a track that was already there keeps whoever added it, and one that was not belongs
    ///     to <paramref name="adder" />. A reorder therefore changes nobody's name.
    /// </summary>
    private static List<TrackSnapshot> Attribute(List<TrackSnapshot> old, List<TrackSnapshot> next, string? adder)
    {
        // ponytail: matched by id, first unused match wins; a duplicated id inherits in order
        var pool = old.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => new Queue<string?>(g.Select(t => t.AddedBy)));
        foreach (var track in next)
            track.AddedBy = pool.TryGetValue(track.Id, out var added) && added.Count > 0 ? added.Dequeue() : adder;

        return next;
    }

    /// <summary>
    ///     Takes <paramref name="collaborator" /> off every playlist <paramref name="owner" /> owns, and
    ///     returns the ones it was on. Caller holds <see cref="_gate" />.
    /// </summary>
    private List<Playlist> DropCollaboratorLocked(User owner, string collaborator)
    {
        var changed = _playlists.Values
            .Where(p => p.OwnerKey == owner.Key && p.Collaborators.Any(c => Same(c, collaborator))).ToList();
        foreach (var playlist in changed)
            playlist.Collaborators = [.. playlist.Collaborators.Where(c => !Same(c, collaborator))];

        return changed;
    }

    /// <summary>A code as somebody typed or read it: case, dashes and the look-alikes Crockford folds together.</summary>
    internal static string NormalizeCode(string? code) =>
        new([.. (code ?? "").ToUpperInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c })]);

    /// <summary>A live invite by its code, however it was typed. Caller holds <see cref="_gate" />.</summary>
    private Invite? FindInviteLocked(string? code) =>
        _invites.TryGetValue(NormalizeCode(code), out var invite) && invite.ExpiresUtc > DateTimeOffset.UtcNow
            ? invite
            : null;

    /// <summary>The account's unexpired invite, sweeping the dead ones while it looks. Caller holds <see cref="_gate" />.</summary>
    private Invite? LiveInviteLocked(User user)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var dead in _invites.Values.Where(i => i.ExpiresUtc <= now).ToList()) _invites.Remove(dead.Code);

        return _invites.Values.FirstOrDefault(i => ReferenceEquals(i.Owner, user));
    }

    /// <summary>Caller holds <see cref="_gate" />.</summary>
    /// <returns>Why the rename was refused, or <c>null</c> when it happened.</returns>
    private Refusal? RenameLocked(User user, string? newName)
    {
        if (!IsValidUsername(newName, out var invalid)) return new Refusal(Failure.InvalidRequest, invalid);

        var name = newName.Trim();
        var oldKey = user.Key;
        var newKey = User.Normalize(name);
        if (newKey != oldKey && _users.ContainsKey(newKey)) return UsernameTaken;

        // Playlist.Owner holds the display name and OwnerKey derives from it, so the playlists
        // have to move with the account or every one of them orphans on rename.
        var changed = _playlists.Values.Where(playlist => playlist.OwnerKey == oldKey).ToHashSet();
        foreach (var playlist in changed) playlist.Owner = name;

        // Friends, collaborators and who added what hold the display name too.
        // ponytail: every track of every playlist; a rename is rare and this is the global lock anyway
        var befriended = _users.Values.Where(other => other.Friends.Any(f => User.Normalize(f) == oldKey)).ToList();
        foreach (var other in befriended)
            other.Friends = [.. other.Friends.Select(f => User.Normalize(f) == oldKey ? name : f)];

        foreach (var playlist in _playlists.Values)
        {
            if (playlist.Collaborators.Any(c => User.Normalize(c) == oldKey))
            {
                playlist.Collaborators = [.. playlist.Collaborators.Select(c => User.Normalize(c) == oldKey ? name : c)];
                changed.Add(playlist);
            }

            foreach (var track in playlist.Tracks.Where(t => t.AddedBy is { } by && User.Normalize(by) == oldKey))
            {
                track.AddedBy = name;
                changed.Add(playlist);
            }
        }

        _users.Remove(oldKey);
        user.Username = name;
        _users[user.Key] = user;

        // the old key goes first: a rename that only changes case keeps the key, and its row comes straight back
        SaveLocked(users: [user, .. befriended], playlists: changed, goneUsers: [oldKey]);
        return null;
    }

    /// <summary>Caller holds <see cref="_gate" />. Returns the orphaned cover files and how many playlists went.</summary>
    private (List<string> covers, int playlists) DeleteLocked(User user)
    {
        var owned = _playlists.Values.Where(playlist => playlist.OwnerKey == user.Key).ToList();
        foreach (var playlist in owned) _playlists.Remove(playlist.Id);

        // gone from every friend list and every playlist it could edit; the tracks it added keep its name
        var befriended = _users.Values.Where(other => IsFriend(other, user.Username)).ToList();
        foreach (var other in befriended)
            other.Friends = [.. other.Friends.Where(f => !Same(f, user.Username))];
        var collaborated = _playlists.Values.Where(playlist => Collaborates(playlist, user)).ToList();
        foreach (var playlist in collaborated)
            playlist.Collaborators = [.. playlist.Collaborators.Where(c => User.Normalize(c) != user.Key)];
        foreach (var invite in _invites.Values.Where(invite => ReferenceEquals(invite.Owner, user)).ToList())
            _invites.Remove(invite.Code);

        RevokeLocked(user);
        _users.Remove(user.Key);

        SaveLocked(users: befriended, playlists: collaborated, goneUsers: [user.Key],
            gonePlaylists: owned.Select(playlist => playlist.Id));
        return (owned.Select(p => p.CoverFile).OfType<string>().ToList(), owned.Count);
    }

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
                users = _users.Values.Select(user => new
                {
                    username = user.Username,
                    createdUtc = user.CreatedUtc,
                    activeTokens = user.Tokens.Count(token => token.ExpiresUtc > now),
                    friends = user.Friends.Count,
                    playlists = counts.GetValueOrDefault(user.Key, 0)
                }).OrderBy(user => user.username).ToList(),
                playlists = _playlists.Values.Select(playlist => new
                {
                    id = playlist.Id,
                    name = playlist.Name,
                    owner = playlist.Owner,
                    visibility = playlist.Visibility,
                    collaborators = playlist.Collaborators.Count,
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
            SaveLocked(playlists: [playlist]);
        }
    }

    /// <summary>
    ///     What a playlist has to be. There is no track cap; Kestrel's 30 MB request body limit is
    ///     the only ceiling on how big one save can be.
    /// </summary>
    private static bool IsValidPlaylist([NotNullWhen(true)] string? name, List<TrackSnapshot>? tracks,
        [NotNullWhen(false)] out string? message)
    {
        var trimmed = name?.Trim() ?? "";

        message = trimmed.Length is < 1 or > 80 ? "A playlist name is between 1 and 80 characters."
            : trimmed.Any(char.IsControl) ? "A playlist name cannot contain control characters."
            : tracks is not null &&
              tracks.Any(t => string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name))
                ? "Every track needs an id and a name."
                : null;

        return name is not null && message is null;
    }

    /// <summary>Trims what came off the wire down to the fields a row draws, and nothing else.</summary>
    private static List<TrackSnapshot> Clean(List<TrackSnapshot>? tracks) =>
    [
        .. (tracks ?? []).Select(t => new TrackSnapshot
        {
            Id = t.Id.Trim(),
            Name = t.Name.Trim(),
            Artist = t.Artist.Trim(),
            Album = string.IsNullOrWhiteSpace(t.Album) ? null : t.Album.Trim(),
            Duration = TimeSpan.TryParse(t.Duration, CultureInfo.InvariantCulture, out var length)
                ? length.ToString("c", CultureInfo.InvariantCulture)
                : "00:00:00",
            ThumbnailUrl = string.IsNullOrWhiteSpace(t.ThumbnailUrl) ? null : t.ThumbnailUrl.Trim()
        })
    ];

    /// <summary>
    ///     What a username has to be. Deliberately permissive about script — the library this fronts is
    ///     largely Cyrillic-tagged and a Latin-only rule would be the wrong kind of tidy — and strict about
    ///     the things that actually cause trouble: whitespace and control characters. Separate from the
    ///     password rule because an admin rename changes one without the other.
    /// </summary>
    private static bool IsValidUsername([NotNullWhen(true)] string? username, [NotNullWhen(false)] out string? message)
    {
        var name = username?.Trim() ?? "";

        message = name.Length is < 2 or > 32 ? "A username is between 2 and 32 characters."
            : name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? "A username cannot contain spaces."
            : null;

        return username is not null && message is null;
    }

    /// <summary>Bounded both ways: an unbounded password is an unbounded PBKDF2.</summary>
    private static bool IsValidPassword([NotNullWhen(true)] string? password, [NotNullWhen(false)] out string? message)
    {
        message = password is null || password.Length < 8 ? "A password is at least 8 characters."
            : password.Length > 256 ? "A password is at most 256 characters."
            : null;

        return password is not null && message is null;
    }

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

    /// <summary>A new <see cref="User.Id" />.</summary>
    internal static string NewId() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    ///     Writes the given accounts and playlists as they now are and drops the gone ones. Caller holds
    ///     <see cref="_gate" />.
    /// </summary>
    private void SaveLocked(IEnumerable<User>? users = null, IEnumerable<Playlist>? playlists = null,
        IEnumerable<string>? goneUsers = null, IEnumerable<string>? gonePlaylists = null)
    {
        _database.Save(users ?? [], playlists ?? [], goneUsers ?? [], gonePlaylists ?? []);
    }
}

/// <summary>
///     A live friend code. Never written to the file: see <c>DomStore._invites</c>.
/// </summary>
public sealed class Invite
{
    public required string Code { get; init; }
    public required User Owner { get; init; }
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>Who became a friend through it, in order. Replaced, never mutated.</summary>
    public List<string> Joined { get; set; } = [];
}
