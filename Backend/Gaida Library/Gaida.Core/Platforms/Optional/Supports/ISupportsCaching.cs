using Gaida.Core.Streams;

namespace Gaida.Core.Platforms.Optional.Supports;

/// <summary>A result whose platform keeps what it downloads, given the chance as the download starts.</summary>
public interface ISupportsCaching
{
    public void RunCacheProcess(StreamSpreader streamSpreader);
}