using Maia.API.Auth;
using Maia.API.Extensions;
using Maia.API.Middleware;
using Maia.Core.Configuration;
using Maia.Core.Interfaces;
using Maia.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Logging: Serilog (replaces the default Console+Debug providers) ────────
// Config lives in appsettings.json under "Serilog" — both sinks (console
// for `dotnet run` visibility + rolling daily file at logs/maia-api-.log
// with 30-day retention) are declarative there, so ops can swap rolling
// policy or add a network sink without a rebuild.
builder.Host.UseSerilog((ctx, services, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .ReadFrom.Services(services)
       .Enrich.FromLogContext());

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalhost", policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://localhost:5173",
                "http://localhost:4200",
                "http://127.0.0.1:5095"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            // Required for the browser to send/receive the httpOnly session cookie
            // cross-origin. Compatible only with explicit origins (above), not "*".
            .AllowCredentials();
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── HTTP client for ApiCallExecutor ─────────────────────────────────────────
builder.Services.AddHttpClient("FixEngine");

// ── Infrastructure: DB, repos, strategies, parsers, workers ─────────────────
builder.Services.AddMaia(
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing."));

// ── Application: use cases (registered as interfaces for testability) ────────
builder.Services.AddApplicationServices();

// ── Authentication: server-side opaque-token sessions in an httpOnly cookie ──
// Phase 1 is authn-OPEN: the handler populates the principal when a valid cookie
// is present but never rejects anonymous (no policies / fallback yet). Enforcement
// lands in Phase 3 by adding policies + a fallback policy here — no handler change.
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(authOptions);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();
builder.Services
    .AddAuthentication(MaiaSessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, MaiaSessionAuthenticationHandler>(
        MaiaSessionAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization(options =>
{
    // Tiered floors — higher roles inherit lower. RequireRole admits only the
    // named roles, so each tier must name every role at or above it.
    options.AddPolicy("RequireUser",     p => p.RequireAuthenticatedUser());
    options.AddPolicy("RequireOperator", p => p.RequireRole("Operator", "Administrator"));
    options.AddPolicy("RequireAdmin",    p => p.RequireRole("Administrator"));

    // Default-CLOSED for authentication: any endpoint missing an explicit
    // [Authorize]/[AllowAnonymous] still requires a logged-in user. This is
    // default-AUTHENTICATED, NOT default-admin — a write that forgets its
    // RequireAdmin would fall through to "any authenticated user" (privilege
    // escalation, not a lockout). The compensating control is the exhaustive
    // (endpoint × verb × role) authorization matrix test, which is the cutover gate.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddHealthChecks();

// ── Global error handling ─────────────────────────────────────────────────────
builder.Services.AddGlobalExceptionHandling();

var app = builder.Build();

// EnableSwagger lets ops expose Swagger in a non-Development environment for
// deployment troubleshooting (e.g. IIS) without switching ASPNETCORE_ENVIRONMENT,
// which would also swap in appsettings.Development.json's connection string.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("EnableSwagger"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseCors("AllowLocalhost");
app.UseHttpsRedirection();
app.UseAuthentication();
// Forces a password rotation before any other /api/* call (after auth, before authz).
app.UseMiddleware<MustChangePasswordMiddleware>();
app.UseAuthorization();
app.MapControllers();

// K8s liveness/readiness — anonymous so they answer under the fallback policy.
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.Run();

// Exposed as a public partial so the integration tests can boot the real pipeline
// via WebApplicationFactory<Program> for the authorization matrix.
public partial class Program;
