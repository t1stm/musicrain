namespace Dom;

/// <summary>Dom's settings, under the keys it has always read.</summary>
public sealed class DomOptions
{
    [ConfigurationKeyName("Dom:DataFile")]
    public string DataFile { get; set; } = "dom.db";

    /// <summary>Where playlist covers are kept.</summary>
    [ConfigurationKeyName("Dom:CoverDir")]
    public string CoverDir { get; set; } = "covers";

    /// <summary>Gaida.API, asked once a day what every playlist's tracks are called now. Unset skips it.</summary>
    [ConfigurationKeyName("Gaida:Url")]
    public string? GaidaUrl { get; set; }

    /// <summary>Moliv, told when an account is deleted. Unset skips it.</summary>
    [ConfigurationKeyName("Moliv:Url")]
    public string? MolivUrl { get; set; }

    /// <summary>Issue a new administrator key at boot when none is waiting. Unset it once the key is claimed.</summary>
    [ConfigurationKeyName("Dom:NewAdminKey")]
    public bool NewAdminKey { get; set; }

    [ConfigurationKeyName("ADMIN_TOKEN")]
    public string? AdminToken { get; set; }
}
