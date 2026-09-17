using JetBrains.Annotations;

namespace Gaida.Core.Platforms.Optional.Supports;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface ISupportsRandomResults
{
    public IAsyncEnumerable<PlatformResult> GetRandomResults(int count,
        CancellationToken cancellationToken = default);
}
