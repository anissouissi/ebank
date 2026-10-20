using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

var authority = builder.Configuration["Keycloak:Authority"]
    ?? throw new InvalidOperationException("Keycloak:Authority must be configured.");
var issuer = builder.Configuration["Keycloak:Issuer"]
    ?? throw new InvalidOperationException("Keycloak:Issuer must be configured.");
var backchannelAuthority = new Uri(
    builder.Configuration["Keycloak:BackchannelAuthority"] ?? issuer);
var audience = builder.Configuration["Keycloak:Audience"]
    ?? throw new InvalidOperationException("Keycloak:Audience must be configured.");
var apiBaseUrl = builder.Configuration["EbankApi:BaseUrl"]
    ?? throw new InvalidOperationException("EbankApi:BaseUrl must be configured.");

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<EbankApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
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
            NameClaimType = "preferred_username",
            ValidIssuer = issuer
        };
        options.Backchannel = new HttpClient(new KeycloakBackchannelHandler(
            new Uri(issuer),
            backchannelAuthority));
    });
builder.Services.AddAuthorization();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<EbankReadTools>()
    .AddAuthorizationFilters();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" }))
    .AllowAnonymous();
app.MapMcp("/mcp").RequireAuthorization();

app.Run();
