using Dom;
using Dom.Store;
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

// Wide open, matching the rest of the stack — but for a different reason, so it is worth saying
// out loud. Dunav's CORS note ("no credentials to protect") does not hold here: these endpoints
// carry passwords and bearer tokens. What makes the policy safe is that the credential is an
// explicit Authorization header the caller has to already possess, never an ambient cookie, so a
// hostile origin gains nothing by being allowed to ask. Cookies must never be added here without
// replacing this policy with an origin list.
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddSingleton(Log.Logger);
builder.Services.AddSingleton(services => new DomStore(
    services.GetRequiredService<IConfiguration>()["Dom:DataFile"] ?? "dom.db",
    Log.Logger));

var app = builder.Build();

app.UseCors("Frontend");

// load the accounts file at boot, so a corrupt one fails the container rather than the first login
app.Services.GetRequiredService<DomStore>();

// Before MapControllers so the request ring wraps the whole pipeline. No-op without ADMIN_TOKEN.
app.MapDomAdmin();

app.MapControllers();
app.Run();
return 0;
