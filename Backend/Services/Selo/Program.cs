using Selo;
using Selo.Multiplayer;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

if (args.Contains("--self-check"))
{
    await SelfCheck.RunAsync();
    return;
}

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new ExpressionTemplate(
        "[{@t:HH:mm:ss} {@l:u3}" +
        "{#if SourceContext is not null} {Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1)}{#end}] {@m}\n{@x}",
        theme: TemplateTheme.Code))
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSerilog();
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddSingleton(Log.Logger);

// Room.cs's one coupling to audio: resolving an `add <id>` through Gaida.API's public
// /Audio/Search instead of an in-process AudioManager.
var gaidaUrl = builder.Configuration["Gaida:Url"]
               ?? throw new InvalidOperationException("Gaida:Url must be configured.");
builder.Services.AddHttpClient(MultiplayerManager.Gaida, http => http.BaseAddress = new Uri(gaidaUrl));
builder.Services.AddSingleton<MultiplayerManager>();

var app = builder.Build();

app.UseCors("Frontend");

app.UseWebSockets(new WebSocketOptions
{
    // Quickly disconnect timed out connections to avoid deadlocks.
    KeepAliveInterval = TimeSpan.FromSeconds(1),
    KeepAliveTimeout = TimeSpan.FromSeconds(5)
});

// initialize required services here.
app.Services.GetRequiredService<MultiplayerManager>();

// Before MapControllers so the request ring wraps the whole pipeline. No-op without ADMIN_TOKEN.
app.MapSeloAdmin();

app.UseAuthorization();
app.MapControllers();
app.Run();