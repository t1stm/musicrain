using System.Text.Json.Serialization;
using Gaida.Core.Platforms;

namespace Gaida.Platforms.MusicDatabase;

public class MusicResult : PlatformResult
{
    public const string Prefix = "audio://";

    [JsonIgnore] public string Path { get; init; } = string.Empty;
}