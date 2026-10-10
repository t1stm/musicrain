using Gaida.Admin;

namespace Dom;

/// <summary>
///     What a deleted account leaves behind, whoever deleted it — its owner or an operator: its playlists'
///     covers and its listening history. One place, so the two deletes of an account cannot leave different
///     things behind. Best effort, both: the deletion already happened and stands either way.
/// </summary>
public sealed class AccountCleanup(
    CoverFiles covers,
    IHttpClientFactory http,
    IConfiguration configuration,
    ILogger<AccountCleanup> log)
{
    /// <summary>The named client Moliv is called with.</summary>
    public const string Moliv = "moliv";

    public async Task AfterDeleteAsync(string userId, IEnumerable<string> coverFiles)
    {
        foreach (var cover in coverFiles) covers.Forget(cover);
        await ForgetHistoryAsync(userId);
    }

    /// <summary>
    ///     Has Moliv drop the account's listening history. Unset <c>Moliv:Url</c> skips it. Deliberately not
    ///     tied to the request: a client leaving after the delete must not leave the history behind.
    /// </summary>
    /// <remarks>
    ///     ponytail: Moliv down at this moment leaves the account's plays orphaned for good — rows under an
    ///     ID nobody can sign in as. Give Oko a forget button if the log ever shows one.
    /// </remarks>
    private async Task ForgetHistoryAsync(string userId)
    {
        var moliv = configuration["Moliv:Url"];
        if (string.IsNullOrWhiteSpace(moliv)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{moliv.TrimEnd('/')}/forget?user={Uri.EscapeDataString(userId)}");
            if (configuration["ADMIN_TOKEN"] is { Length: > 0 } token) request.Headers.Add(AdminApi.TokenHeader, token);

            using var response = await http.CreateClient(Moliv).SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(exception, "Moliv did not forget the history of account {Id}", userId);
        }
    }
}
