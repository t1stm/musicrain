namespace Dom;

/// <summary>Dom's settings, under the keys it has always read.</summary>
public sealed class DomOptions
{
    [ConfigurationKeyName("Dom:DataFile")]
    public string DataFile { get; set; } = "dom.db";

    /// <summary>Where playlist covers are kept.</summary>
    [ConfigurationKeyName("Dom:CoverDir")]
    public string CoverDir { get; set; } = "covers";

    /// <summary>Moliv, told when an account is deleted. Unset skips it.</summary>
    [ConfigurationKeyName("Moliv:Url")]
    public string? MolivUrl { get; set; }

    [ConfigurationKeyName("ADMIN_TOKEN")]
    public string? AdminToken { get; set; }
}
