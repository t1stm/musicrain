using System.Text.Encodings.Web;
using System.Text.Json;

namespace Gaida.Core.Platforms;

public static class CustomSerializer
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}