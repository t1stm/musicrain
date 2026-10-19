using System.Globalization;
using Microsoft.Extensions.FileProviders.Physical;
using SixLabors.ImageSharp.Web.Caching;
using SixLabors.ImageSharp.Web.Commands;
using SixLabors.ImageSharp.Web.Providers;
using SixLabors.ImageSharp.Web.Resolvers;

namespace Snimka;

// The three pieces ImageSharp.Web needs told about this service: where a request's image comes from, what
// its query string means, and what the resize is cached under. Everything else is the library's own.

/// <summary>Hands ImageSharp.Web the file behind <c>url=</c>, fetching a remote one first if it has to.</summary>
public sealed class CoverProvider(Images images) : IImageProvider
{
    /// <summary>Only a request with a size is ImageSharp.Web's; one without falls through to the plain file.</summary>
    public ProcessingBehavior ProcessingBehavior => ProcessingBehavior.CommandOnly;

    public Func<HttpContext, bool> Match { get; set; } = context => context.Request.Path == Images.Route;

    public bool IsValidRequest(HttpContext context)
    {
        return images.Parse(context.Request.Query["url"]) is not null;
    }

    public async Task<IImageResolver?> GetAsync(HttpContext context)
    {
        return await images.ResolveAsync(context.Request.Query["url"], context.RequestAborted) is { } path
            ? new FileProviderImageResolver(new PhysicalFileInfo(new FileInfo(path)))
            : null;
    }
}

/// <summary>
///     <c>size=</c> is the whole command language: a square of that many pixels, cropped to fill. Every other key is
///     ignored, so new options can be added later without old URLs changing meaning.
/// </summary>
public sealed class SizeParser : IRequestParser
{
    private const int Step = 32;
    private const int Smallest = 32;
    private const int Largest = 1024;

    public CommandCollection ParseRequestCommands(HttpContext context)
    {
        var commands = new CommandCollection();
        if (Snap(context.Request.Query["size"]) is not { } size) return commands;

        commands.Add("width", size);
        commands.Add("height", size);
        commands.Add("rmode", "crop");
        commands.Add("autoorient", "true");
        return commands;
    }

    /// <summary>
    ///     Rounded up to a multiple of 32 between 32 and 1024, so a stranger asking for every width there is gets
    ///     at most 32 copies of an image rather than a disk full of them.
    /// </summary>
    public static string? Snap(string? value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pixels) || pixels <= 0)
            return null;

        var snapped = Math.Clamp((pixels + Step - 1) / Step * Step, Smallest, Largest);
        return snapped.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
///     The image and the commands it was made with, and nothing else from the query: <c>size=100</c> and
///     <c>size=101</c> are one 128px copy, not two. Case kept, because a YouTube ID is case-sensitive.
/// </summary>
public sealed class CoverCacheKey : ICacheKey
{
    public string Create(HttpContext context, CommandCollection commands)
    {
        return context.Request.Query["url"] + "?" + string.Join('&', commands.Select(command => $"{command.Key}={command.Value}"));
    }
}
