using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ModelContextProtocol.Client;
using Ebank.Api.Data;
using Ebank.Api.Endpoints;
using Ebank.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var authority = builder.Configuration["Keycloak:Authority"]
    ?? throw new InvalidOperationException("Keycloak:Authority must be configured.");
var audience = builder.Configuration["Keycloak:Audience"]
    ?? throw new InvalidOperationException("Keycloak:Audience must be configured.");
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? [];
var connectionString = builder.Configuration.GetConnectionString("Ebank")
    ?? throw new InvalidOperationException("ConnectionStrings:Ebank must be configured.");
var documentEncryptionService = new DocumentEncryptionService(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<EbankDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton(documentEncryptionService);
builder.Services.AddScoped<IKycService, KycService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IOverviewService, OverviewService>();
builder.Services.AddScoped<IAssistantService, AssistantService>();
var modelEndpoint = builder.Configuration["Assistant:ModelEndpoint"]
    ?? throw new InvalidOperationException("Assistant:ModelEndpoint must be configured.");
var modelName = builder.Configuration["Assistant:Model"]
    ?? throw new InvalidOperationException("Assistant:Model must be configured.");
var modelApiKey = builder.Configuration["Assistant:ApiKey"]
    ?? throw new InvalidOperationException("Assistant:ApiKey must be configured.");
builder.Services.AddSingleton<IChatClient>(_ =>
    new ChatClient(
        modelName,
        new ApiKeyCredential(modelApiKey),
        new OpenAIClientOptions
        {
            Endpoint = new Uri(modelEndpoint),
            NetworkTimeout = TimeSpan.FromMinutes(3)
        }).AsIChatClient());
builder.Services.AddSingleton(new AssistantMcpOptions(
    new Uri(builder.Configuration["Assistant:McpEndpoint"]
        ?? throw new InvalidOperationException("Assistant:McpEndpoint must be configured."))));
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = builder.Configuration.GetValue(
            "Keycloak:RequireHttpsMetadata",
            true);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "preferred_username"
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<
                    ILoggerFactory>().CreateLogger("KeycloakTokenValidation");
                logger.LogWarning(context.Exception,
                    "Keycloak access-token validation failed ({FailureType}).",
                    context.Exception.GetType().Name);
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<
                    ILoggerFactory>().CreateLogger("KeycloakTokenValidation");
                var failureType = context.AuthenticateFailure?.GetType().Name;
                logger.LogWarning(
                    "Bearer authentication challenge for {Path}. Failure type: {FailureType}.",
                    context.Request.Path,
                    failureType ?? "NoAuthenticationFailure");

                if (context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>()
                    .IsDevelopment())
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        title = "Unauthorized",
                        status = StatusCodes.Status401Unauthorized,
                        detail = failureType is null
                            ? "No bearer token was accepted. Verify the Authorization header."
                            : $"The access token failed validation ({failureType}). See the API terminal for details."
                    });
                }
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.Logger.LogInformation(
    "Keycloak JWT validation configured for issuer {Authority} and audience {Audience}.",
    authority,
    audience);

app.UseCors();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

app.MapGet("/api/me", (ClaimsPrincipal user) => Results.Ok(new
{
    subject = user.FindFirstValue("sub"),
    username = user.FindFirstValue("preferred_username"),
    firstName = user.FindFirstValue("given_name"),
    lastName = user.FindFirstValue("family_name")
}))
.RequireAuthorization();

app.MapKycEndpoints();
app.MapAccountEndpoints();
app.MapTransactionEndpoints();
app.MapOverviewEndpoints();
app.MapAssistantEndpoints();

app.Run();
