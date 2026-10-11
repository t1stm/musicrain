namespace Selo;

/// <summary>Selo's settings, under the keys it has always read.</summary>
public sealed class SeloOptions
{
    /// <summary>The Gaida.API a room resolves an <c>add &lt;id&gt;</c> through. Selo will not start without it.</summary>
    [ConfigurationKeyName("Gaida:Url")]
    public string? GaidaUrl { get; set; }
}
