using Gaida.Bot.Gaida;
using Gaida.Bot.Players;

namespace Gaida.Bot;

/// <summary>
/// What every account's service provider needs. Each client builds its own provider, so the three
/// shared objects are registered as instances rather than types — registering the types would hand
/// each account a player registry of its own, and they would never see each other's players.
/// </summary>
public static class BotServices
{
    public static IServiceCollection Register(IServiceCollection services, ILogger logger, GaidaClient api,
        PlayerController controller)
    {
        // As ILogger, not as Serilog.Core.Logger: the command class asks the container for the
        // interface, and inference off CreateLogger() would register only the concrete type.
        services.AddSingleton(logger);
        services.AddSingleton(api);
        services.AddSingleton(controller);

        return services;
    }
}
