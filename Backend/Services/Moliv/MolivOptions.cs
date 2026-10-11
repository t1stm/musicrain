namespace Moliv;

/// <summary>Moliv's settings, under the keys it has always read.</summary>
public sealed class MolivOptions
{
    [ConfigurationKeyName("Moliv:DataFile")]
    public string DataFile { get; set; } = "plays.db";

    /// <summary>Dom, asked who a bearer token belongs to.</summary>
    [ConfigurationKeyName("Dom:Url")]
    public string DomUrl { get; set; } = "http://dom:8080";

    [ConfigurationKeyName("ADMIN_TOKEN")]
    public string? AdminToken { get; set; }
}
