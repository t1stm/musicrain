using Dunav;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new ExpressionTemplate(
        "[{@t:HH:mm:ss} {@l:u3}" +
        "{#if SourceContext is not null} {Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1)}{#end}] {@m}\n{@x}",
        theme: TemplateTheme.Code))
    .CreateLogger();

if (args.Contains("--self-check")) return await SelfCheck.RunAsync() ? 0 : 1;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSerilog();

// Wide open on purpose, matching Gaida.API: everything served here is public read-only audio and
// there are no cookies or credentials to protect.
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHttpClient("Gaida", (services, http) =>
{
    http.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Gaida:Url"]
                               ?? "http://localhost:8080");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Docker DNS round-robins gaida-api across replicas, but the default pool pins to whichever IP it
    // first resolved for up to 600s -- see SERVICE_SPLIT_PLAN.md "ffmpeg CPU" scaling note.
    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
});

builder.Services.AddSingleton(Log.Logger);
builder.Services.AddSingleton<CacheService>(services =>
    new CacheService(services.GetRequiredService<IHttpClientFactory>().CreateClient("Gaida"),
        Log.Logger, services.GetRequiredService<IConfiguration>()));
builder.Services.AddHostedService(services => services.GetRequiredService<CacheService>());

var app = builder.Build();

app.UseCors("Frontend");

// Before MapControllers so the request ring wraps the whole pipeline. No-op without ADMIN_TOKEN.
app.MapDunavAdmin();

app.MapControllers();
app.Run();
return 0;