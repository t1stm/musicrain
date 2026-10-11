using Gaida.Admin;
using Gaida.API;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

// `dotnet run -- --self-check` runs the pure-logic check in SelfCheck.cs without needing a listening host.
if (args.Contains("--self-check"))
{
    SelfCheck.Run();
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
builder.Services.AddOpenApi();
builder.Services.AddSerilog();
builder.Services.AddHttpClient();
// Wide open on purpose: everything served here is public read-only audio and
// there are no cookies or credentials to protect, while an allowlist quietly
// costs us the Discord activity, every preview deploy and every LAN device.
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHttpClient(ThumbnailCache.Client, http => http.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddOptions<GaidaApiOptions>().Bind(builder.Configuration);
builder.Services.AddSingleton(Log.Logger);
builder.Services.AddSingleton<ManagerService>();
builder.Services.AddSingleton<PlayableResolver>();
builder.Services.AddSingleton<ThumbnailCache>();

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseCors("Frontend");

// Builds the platform pod list from config; nothing to warm up otherwise, since Gaida.API holds no cache.
app.Services.GetRequiredService<ManagerService>();

// Gaida.API holds no cache of its own, so the request ring is the whole payload. No-op without
// ADMIN_TOKEN. See ADMIN_PLAN.md.
app.MapAdmin(() => new { service = "gaida-api" });

app.UseAuthorization();
app.MapControllers();
app.Run();
return;
