using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
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

builder.Configuration["Payments:Provider"] =
    Environment.GetEnvironmentVariable("DFY_PAYMENT_PROVIDER")
    ?? builder.Configuration["Payments:Provider"]
    ?? "Paystack";

builder.Configuration["Paystack:SecretKey"] =
    Environment.GetEnvironmentVariable("PAYSTACK_SECRET_KEY")
    ?? builder.Configuration["Paystack:SecretKey"];

builder.Configuration["Paystack:BaseUrl"] =
    Environment.GetEnvironmentVariable("PAYSTACK_BASE_URL")
    ?? builder.Configuration["Paystack:BaseUrl"]
    ?? "https://api.paystack.co";

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
if (!builder.Environment.IsDevelopment() &&
    string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("DefaultConnection")))
    throw new InvalidOperationException("DefaultConnection must be configured outside Development.");

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

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

        var allOrigins = allowedOrigins.Concat(devOrigins).Distinct().ToArray();

        policy.WithOrigins(allOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});


builder.Services.AddHttpClient("Paystack");
builder.Services.AddScoped<IPaymentProvider, PaystackPaymentProvider>();
builder.Services.AddScoped<IPayoutProvider, PaystackPayoutProvider>();
builder.Services.AddScoped<IBankProvider, PaystackBankProvider>();
builder.Services.AddHttpClient("OzowPayment");
builder.Services.AddScoped<IOzowPaymentService, OzowPaymentService>();
builder.Services.AddHttpClient("OzowOne");

builder.Services.AddHttpClient("OzowPayout");

builder.Services.AddScoped<OzowPayoutHashService>();
builder.Services.AddScoped<IOzowPayoutService, OzowPayoutService>();
builder.Services.AddScoped<IOzowBankService, OzowBankService>();
builder.Services.AddHostedService<PayoutProcessorService>();

var app = builder.Build();

// Database migrations are intentionally NOT executed at application startup.
// Schema changes are applied explicitly and separately after code validation.

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
app.Run($"http://0.0.0.0:{port}");