namespace Gaida.Core.Platforms.Optional.Supports;

public interface ISupportsId
{
    /// <returns>The result, or <c>null</c> when the ID isn't found.</returns>
    public Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
}