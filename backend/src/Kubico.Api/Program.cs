using System.Text;

using Kubico.Api.Activity;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Calendar.Sync;
using Kubico.Api.Chores;
using Kubico.Api.Common;
using Kubico.Api.Households;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Api.Support;
using Kubico.Domain.Users;
using Kubico.Infrastructure;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Fora de Development não se arranca com a configuração de desenvolvimento (chave do JWT do repositório, CORS
// aberto) nem sem as definições obrigatórias de produção.
ProductionChecks.Validate(builder.Configuration, builder.Environment);

// --- Serviços -------------------------------------------------------------

builder.Services.AddKubicoPersistence(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<HouseholdAccess>();
builder.Services.AddScoped<HouseholdNotifier>();
builder.Services.AddSingleton<HubConnections>();
builder.Services.AddScoped<HouseholdGroups>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<ActivityLog>();
builder.Services.AddOptions<SupportOptions>().Bind(builder.Configuration.GetSection(SupportOptions.SectionName));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddKubicoRateLimits(builder.Configuration);
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<NotificationDispatcher>();

// Avisos no telemóvel: Firebase Cloud Messaging quando há conta de serviço (Push:Fcm:ServiceAccountJson);
// sem ela, os avisos ficam só na app.
builder.Services.AddOptions<FcmOptions>().Bind(builder.Configuration.GetSection(FcmOptions.SectionName));
builder.Services.AddHttpClient(nameof(FcmPushSender));
builder.Services.AddSingleton<IPushSender>(sp =>
{
    var fcm = sp.GetRequiredService<IOptions<FcmOptions>>();
    return fcm.Value.IsConfigured
        ? ActivatorUtilities.CreateInstance<FcmPushSender>(
            sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FcmPushSender)))
        : ActivatorUtilities.CreateInstance<LogPushSender>(sp);
});
if (builder.Configuration.GetValue("Notifications:DispatchEnabled", true))
{
    builder.Services.AddHostedService<NotificationWorker>();
}

// Sincronização com o Google Calendar e o Outlook: as credenciais OAuth vêm de CalendarSync:Google e
// CalendarSync:Microsoft; sem elas, o serviço aparece como indisponível. Os tokens guardados são cifrados com
// chaves que ficam na base de dados (têm de sobreviver a reinícios), elas próprias cifradas com a chave-mestra
// DataProtection:MasterKey, que fica fora da base de dados.
builder.Services.AddKubicoDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddOptions<CalendarSyncOptions>().Bind(builder.Configuration.GetSection(CalendarSyncOptions.SectionName));
builder.Services.AddHttpClient<IProductCatalog, OpenFoodFactsCatalog>(http =>
{
    http.DefaultRequestHeaders.UserAgent.ParseAdd(OpenFoodFactsCatalog.UserAgent);
    http.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddHttpClient<GoogleCalendarProvider>();
builder.Services.AddHttpClient<MicrosoftCalendarProvider>();
builder.Services.AddTransient<ICalendarProvider>(sp => sp.GetRequiredService<GoogleCalendarProvider>());
builder.Services.AddTransient<ICalendarProvider>(sp => sp.GetRequiredService<MicrosoftCalendarProvider>());
builder.Services.AddHttpClient<AppleCalendarProvider>(http =>
{
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Kubico/1.0");
    http.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<ICalendarProvider>(sp => sp.GetRequiredService<AppleCalendarProvider>());
builder.Services.AddSingleton<CalendarSecrets>();
builder.Services.AddSingleton<TwoFactorService>();
builder.Services.AddSingleton<CalendarSyncLocks>();
builder.Services.AddSingleton<CalendarSyncTrigger>();
builder.Services.AddScoped<CalendarSyncService>();
if (builder.Configuration.GetValue("CalendarSync:Enabled", true))
{
    builder.Services.AddHostedService<CalendarSyncWorker>();
}
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwt) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.SigningKey)),
            NameClaimType = "name",

            // A tolerância por omissão (5 minutos) deixava um token servir 20 minutos depois de terminar a sessão.
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // O SignalR em WebSocket não envia cabeçalhos: o token chega na query string.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// Os enums viajam como texto e só se aceitam valores que existem: um número ou nome fora do enum (9, "Hourly") é um
// pedido inválido (400), não um valor estranho gravado.
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new DefinedEnumConverter()));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new DefinedEnumConverter()));
builder.Services.AddValidation();

// Pedidos mal formados (sem corpo, JSON inválido, data impossível) e conflitos de concorrência: ProblemDetails com
// título traduzido, em todos os ambientes (sem isto, em Development davam 500).
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
// Os erros vão na língua da app (cabeçalho Accept-Language): o título e as mensagens de validação.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
    Texts.Localize(context.ProblemDetails, Texts.LanguageOf(context.HttpContext)));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<KubicoDbContext>();

// Origens da app web. "http://localhost:*" aceita qualquer porta desse endereço (desenvolvimento: o Flutter web corre
// numa porta aleatória); "*" só é aceite em Development (ver ProductionChecks).
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.SetIsOriginAllowed(origin => CorsOrigins.IsAllowed(origin, allowedOrigins));
    policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// Atrás de um proxy (TLS no proxy): o IP e o esquema verdadeiros vêm dos cabeçalhos X-Forwarded-*, mas só dos
// proxies em ForwardedHeaders:KnownProxies (por omissão, só o próprio servidor).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }
});
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

// --- Pipeline -------------------------------------------------------------

var app = builder.Build();

await app.InitializeDatabaseAsync();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsProduction() && app.Configuration.GetValue("Https:Enforce", true))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSecurityHeaders();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapAccountEndpoints();
api.MapHouseholdEndpoints();
api.MapMemberEndpoints();
api.MapPermissionEndpoints();
api.MapActivityEndpoints();
api.MapAbsenceEndpoints();
api.MapChildProfileEndpoints();
api.MapChildSignInEndpoints();
api.MapShoppingEndpoints();
api.MapProductEndpoints();
api.MapChoreEndpoints();
api.MapChoreHistoryEndpoints();
api.MapCalendarEndpoints();
api.MapCalendarFeedEndpoints();
api.MapCalendarSyncEndpoints();
api.MapRewardEndpoints();
api.MapAllowanceEndpoints();
api.MapNotificationEndpoints();
api.MapFeedbackEndpoints();

// A ligação fecha quando o token de acesso expira (a app volta a ligar com um novo, se a sessão continuar ativa).
app.MapHub<HouseholdHub>(HouseholdHub.Path, options => options.CloseOnAuthenticationExpiration = true);

app.Run();

/// <summary>Exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;

internal static class DatabaseStartup
{
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:MigrateOnStartup", false))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KubicoDbContext>();

        // As migrações são geradas para PostgreSQL; em SQLite cria-se o esquema diretamente.
        if (db.Database.IsSqlite())
        {
            await db.Database.EnsureCreatedAsync();
        }
        else
        {
            await db.Database.MigrateAsync();
        }
    }
}
