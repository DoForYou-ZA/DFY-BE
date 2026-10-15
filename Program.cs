using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DoForYou.API.Data;
using DoForYou.API.Services;
using DoForYou.API.Services.Background;
using DoForYou.API.Middleware;
using DoForYou.API.Hubs;

// Render free instances have a strict inotify/file-descriptor limit. Disable config reload watchers
// so the app does not crash during startup while loading appsettings.json.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE")))
    Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");

var builder = WebApplication.CreateBuilder(args);

// SignalR WebSocket clients must pass access_token in the query string during
// negotiation. ASP.NET Hosting.Diagnostics logs the full request URL at Info,
// which would write bearer tokens into Render logs. Suppress framework request
// URL info logs; keep warnings/errors and application logs enabled.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

builder.Configuration["Ozow:SiteCode"] =
    Environment.GetEnvironmentVariable("OZOW_SITE_CODE")
    ?? builder.Configuration["Ozow:SiteCode"];

builder.Configuration["Ozow:PayoutApiKey"] =
    Environment.GetEnvironmentVariable("OZOW_PAYOUT_API_KEY")
    ?? builder.Configuration["Ozow:PayoutApiKey"];

builder.Configuration["Ozow:PayoutBaseUrl"] =
    Environment.GetEnvironmentVariable("OZOW_PAYOUT_BASE_URL")
    ?? builder.Configuration["Ozow:PayoutBaseUrl"];

builder.Configuration["Ozow:NotifyUrl"] =
    Environment.GetEnvironmentVariable("OZOW_NOTIFY_URL")
    ?? builder.Configuration["Ozow:NotifyUrl"];

builder.Configuration["Ozow:VerifyUrl"] =
    Environment.GetEnvironmentVariable("OZOW_VERIFY_URL")
    ?? builder.Configuration["Ozow:VerifyUrl"];

builder.Configuration["Ozow:AccessToken"] =
    Environment.GetEnvironmentVariable("OZOW_ACCESS_TOKEN")
    ?? builder.Configuration["Ozow:AccessToken"];

builder.Configuration["Ozow:AccountNumberDecryptionKey"] =
    Environment.GetEnvironmentVariable("OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY")
    ?? builder.Configuration["Ozow:AccountNumberDecryptionKey"];

builder.Configuration["Ozow:PayoutIsRtc"] =
    Environment.GetEnvironmentVariable("OZOW_PAYOUT_IS_RTC")
    ?? builder.Configuration["Ozow:PayoutIsRtc"];


builder.Configuration["Ozow:PaymentApiKey"] =
    Environment.GetEnvironmentVariable("OZOW_PAYMENT_API_KEY")
    ?? Environment.GetEnvironmentVariable("OZOW_API_KEY")
    ?? builder.Configuration["Ozow:PaymentApiKey"];

builder.Configuration["Ozow:PaymentPrivateKey"] =
    Environment.GetEnvironmentVariable("OZOW_PAYMENT_PRIVATE_KEY")
    ?? Environment.GetEnvironmentVariable("OZOW_PRIVATE_KEY")
    ?? builder.Configuration["Ozow:PaymentPrivateKey"];

builder.Configuration["Ozow:PaymentBaseUrl"] =
    Environment.GetEnvironmentVariable("OZOW_PAYMENT_BASE_URL")
    ?? builder.Configuration["Ozow:PaymentBaseUrl"]
    ?? "https://stagingapi.ozow.com";

builder.Configuration["Ozow:PaymentIsTest"] =
    Environment.GetEnvironmentVariable("OZOW_PAYMENT_IS_TEST")
    ?? builder.Configuration["Ozow:PaymentIsTest"]
    ?? "true";

builder.Configuration["Ozow:OneClientId"] =
    Environment.GetEnvironmentVariable("OZOW_ONE_CLIENT_ID")
    ?? builder.Configuration["Ozow:OneClientId"];

builder.Configuration["Ozow:OneClientSecret"] =
    Environment.GetEnvironmentVariable("OZOW_ONE_CLIENT_SECRET")
    ?? builder.Configuration["Ozow:OneClientSecret"];

builder.Configuration["Ozow:OneBaseUrl"] =
    Environment.GetEnvironmentVariable("OZOW_ONE_BASE_URL")
    ?? builder.Configuration["Ozow:OneBaseUrl"]
    ?? "https://stagingone.ozow.com/v1";

var jwtKey = builder.Configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY");
if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(jwtKey))
    jwtKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException("JWT_KEY (or Jwt:Key) must be configured outside Development.");
if (!string.IsNullOrWhiteSpace(jwtKey) && jwtKey.Length < 32)
    throw new InvalidOperationException("JWT signing key must be at least 32 characters.");
builder.Configuration["Jwt:Key"] = jwtKey;
var configuredConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuredConnectionString))
    throw new InvalidOperationException("DefaultConnection must be configured outside Development.");

// Render/Neon deployments sometimes provide the standard postgres:// or postgresql:// URI,
// while Npgsql's EF Core provider expects a semicolon-delimited connection string.
// Normalize URI input without logging any connection details or credentials.
if (!string.IsNullOrWhiteSpace(configuredConnectionString) &&
    (configuredConnectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
     configuredConnectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
{
    if (!Uri.TryCreate(configuredConnectionString, UriKind.Absolute, out var databaseUri) ||
        string.IsNullOrWhiteSpace(databaseUri.Host) ||
        string.IsNullOrWhiteSpace(databaseUri.UserInfo))
        throw new InvalidOperationException("DefaultConnection PostgreSQL URI is invalid. Check its host and credentials.");

    var userInfo = databaseUri.UserInfo.Split(':', 2);
    if (userInfo.Length != 2)
        throw new InvalidOperationException("DefaultConnection PostgreSQL URI must include a username and password.");

    var databaseName = Uri.UnescapeDataString(databaseUri.AbsolutePath.TrimStart('/'));
    if (string.IsNullOrWhiteSpace(databaseName))
        throw new InvalidOperationException("DefaultConnection PostgreSQL URI must include a database name.");

    var connectionBuilder = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = databaseUri.Host,
        Port = databaseUri.IsDefaultPort ? 5432 : databaseUri.Port,
        Database = databaseName,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = Uri.UnescapeDataString(userInfo[1]),
        SslMode = Npgsql.SslMode.Require
    };
    configuredConnectionString = connectionBuilder.ConnectionString;
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Stable document name used by CI to export the canonical API contract.
    // Swagger remains served only in Development below; this does not expose it in production.
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "DoForYou API",
        Version = "v1",
        Description = "Canonical HTTP API contract for DFY-FE and dfy-mobile. Business rules remain authoritative in DFY-BE."
    });
});

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(configuredConnectionString));

// Services
builder.Services.AddScoped<IRulesEngine, RulesEngine>();
builder.Services.AddScoped<IEscrowService, EscrowService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IBankingService, BankingService>();
builder.Services.AddScoped<IUserPolicyService, UserPolicyService>();
builder.Services.AddHostedService<EscrowReleaseService>();

// SignalR
builder.Services.AddSignalR();

// JWT Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "DoForYou",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "DoForYou",
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey ?? "development-only-key-change-me-32chars")),
            ClockSkew = TimeSpan.Zero
        };
        // Allow SignalR to receive JWT from query string (required for WebSocket handshake)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/v1/hubs/chat"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var rawUserId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!int.TryParse(rawUserId, out var userId))
                {
                    context.Fail("The token does not identify a valid user.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var isActive = await db.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == userId && !u.IsArchived, context.HttpContext.RequestAborted);
                if (!isActive)
                    context.Fail("The account is archived or no longer active.");
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DevelopmentOnly", policy =>
        policy.RequireAssertion(_ => builder.Environment.IsDevelopment()));
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var allowedOrigins = (builder.Configuration["AllowedOrigin"] ?? "http://localhost:4200")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var devOrigins = builder.Environment.IsDevelopment()
            ? new[] { "http://localhost:3000", "http://localhost:4200", "http://localhost:5173" }
            : Array.Empty<string>();

        // Playwright mobile-responsiveness E2E runs a local Vite preview against
        // the staging API. Allow that origin only when the explicit E2E safety
        // flag is enabled; production CORS remains restricted to configured origins.
        var e2eOrigins = string.Equals(
                Environment.GetEnvironmentVariable("DFY_E2E_TEST_MODE"),
                "true",
                StringComparison.OrdinalIgnoreCase)
            ? new[] { "http://127.0.0.1:4173", "http://localhost:4173" }
            : Array.Empty<string>();

        var allOrigins = allowedOrigins.Concat(devOrigins).Concat(e2eOrigins).Distinct().ToArray();

        policy.WithOrigins(allOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});


builder.Configuration["Paystack:SecretKey"] =
    Environment.GetEnvironmentVariable("PAYSTACK_SECRET_KEY")
    ?? builder.Configuration["Paystack:SecretKey"];

builder.Configuration["Paystack:BaseUrl"] =
    Environment.GetEnvironmentVariable("PAYSTACK_BASE_URL")
    ?? builder.Configuration["Paystack:BaseUrl"]
    ?? "https://api.paystack.co";

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("Paystack");
builder.Services.AddSingleton<PaystackProvider>();
builder.Services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<PaystackProvider>());
builder.Services.AddSingleton<IPayoutProvider>(sp => sp.GetRequiredService<PaystackProvider>());
builder.Services.AddSingleton<IBankProvider>(sp => sp.GetRequiredService<PaystackProvider>());
builder.Services.AddSingleton<IPaystackSettlementProvider>(sp => sp.GetRequiredService<PaystackProvider>());
builder.Services.AddHostedService<PayoutProcessorService>();

var app = builder.Build();

// Migrations remain opt-in. Staging can set AUTO_MIGRATE=true so the deployed
// service applies pending EF migrations (including the Paystack payment-attempt
// table) before serving requests. Production should keep this disabled and
// apply migrations through the release process.
if (string.Equals(Environment.GetEnvironmentVariable("AUTO_MIGRATE"), "true", StringComparison.OrdinalIgnoreCase))
{
    using var migrationScope = app.Services.CreateScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>();
    await migrationDb.Database.MigrateAsync();

    // Staging schema-heal: the existing database's migration history is known to
    // be ahead of its physical schema. Keep this idempotent and staging-only so
    // Paystack columns/tables are guaranteed even when EF thinks migrations ran.
    await migrationDb.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Payments""
        ADD COLUMN IF NOT EXISTS ""Provider"" text NOT NULL DEFAULT 'Ozow';

        ALTER TABLE ""BankAccounts""
        ADD COLUMN IF NOT EXISTS ""BankCode"" text NULL;

        ALTER TABLE ""BankAccounts""
        ADD COLUMN IF NOT EXISTS ""ProviderReference"" text NULL;

        CREATE TABLE IF NOT EXISTS ""PaymentAttempts"" (
            ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ""TaskId"" integer NOT NULL,
            ""Provider"" text NOT NULL,
            ""Reference"" text NOT NULL,
            ""Amount"" numeric NOT NULL,
            ""Status"" text NOT NULL,
            ""PaymentUrl"" text NULL,
            ""TransactionId"" text NULL,
            ""FailureReason"" text NULL,
            ""CreatedAt"" timestamp with time zone NOT NULL,
            ""CompletedAt"" timestamp with time zone NULL
        );

        CREATE INDEX IF NOT EXISTS ""IX_PaymentAttempts_TaskId""
        ON ""PaymentAttempts"" (""TaskId"");

        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PaymentAttempts_Reference""
        ON ""PaymentAttempts"" (""Reference"");

        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PaymentAttempts_TransactionId""
        ON ""PaymentAttempts"" (""TransactionId"")
        WHERE ""TransactionId"" IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_BankAccounts_ProviderReference""
        ON ""BankAccounts"" (""ProviderReference"")
        WHERE ""ProviderReference"" IS NOT NULL;
    ");
}


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/api/v1/hubs/chat");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Open Swagger in browser after application starts
if (app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        var url = $"http://localhost:{port}/swagger";
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", url) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
        }
        catch
        {
            // Ignore if browser can't be opened
        }
    });
}

var port = Environment.GetEnvironmentVariable("PORT") ?? (app.Environment.IsDevelopment() ? "5001" : "8080");
if (app.Environment.IsDevelopment() && string.Equals(Environment.GetEnvironmentVariable("DFY_OPENAPI_EXPORT"), "true", StringComparison.OrdinalIgnoreCase))
    app.Run();
else
    app.Run($"http://0.0.0.0:{port}");