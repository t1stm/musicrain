namespace Oko;

/// <summary>Oko's settings, under the keys it has always read.</summary>
public sealed class OkoOptions
{
    /// <summary>The panel's own sign-in. Oko refuses to start without both.</summary>
    [ConfigurationKeyName("ADMIN_USERNAME")]
    public string? Username { get; set; }

    [ConfigurationKeyName("ADMIN_PASSWORD")]
    public string? Password { get; set; }

    /// <summary>The header every target's /Admin surface wants.</summary>
    [ConfigurationKeyName("ADMIN_TOKEN")]
    public string? AdminToken { get; set; }

    /// <summary>Dom, which says who a staff bearer token belongs to. Oko refuses to start without it.</summary>
    [ConfigurationKeyName("Dom:Url")]
    public string? DomUrl { get; set; }

    public Target[] Targets { get; set; } = [];
}
