using System.Text;
using System.Text.Json.Serialization;
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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- Serviços -------------------------------------------------------------

builder.Services.AddKubicoPersistence(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<HouseholdAccess>();
builder.Services.AddScoped<HouseholdNotifier>();
builder.Services.AddSingleton<HubConnections>();
builder.Services.AddScoped<HouseholdGroups>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddOptions<SupportOptions>().Bind(builder.Configuration.GetSection(SupportOptions.SectionName));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<SessionService>();
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
// chaves que ficam na base de dados (têm de sobreviver a reinícios).
builder.Services.AddDataProtection().SetApplicationName("Kubico").PersistKeysToDbContext<KubicoDbContext>();
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
builder.Services.AddSingleton<CalendarSecrets>();
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

builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<KubicoDbContext>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Contains("*"))
    {
        // Desenvolvimento: a app Flutter web corre numa porta aleatória.
        policy.SetIsOriginAllowed(_ => true);
    }
    else
    {
        policy.WithOrigins(allowedOrigins);
    }

    policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// --- Pipeline -------------------------------------------------------------

var app = builder.Build();

await app.InitializeDatabaseAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapHouseholdEndpoints();
api.MapMemberEndpoints();
api.MapPermissionEndpoints();
api.MapAbsenceEndpoints();
api.MapChildProfileEndpoints();
api.MapChildSignInEndpoints();
api.MapShoppingEndpoints();
api.MapProductEndpoints();
api.MapChoreEndpoints();
api.MapCalendarEndpoints();
api.MapCalendarFeedEndpoints();
api.MapCalendarSyncEndpoints();
api.MapRewardEndpoints();
api.MapNotificationEndpoints();
api.MapFeedbackEndpoints();

app.MapHub<HouseholdHub>(HouseholdHub.Path);

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
