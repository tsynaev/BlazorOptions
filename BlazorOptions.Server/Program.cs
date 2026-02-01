using BlazorOptions.Server.Authentication;
using BlazorOptions.Server.Models;
using BlazorOptions.Server.Options;
using BlazorOptions.Server.Services;
using BlazorOptions.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.Configure<DataStorageOptions>(
    builder.Configuration.GetSection(DataStorageOptions.SectionName));
builder.Services.AddSingleton<UserRegistryService>();
builder.Services.AddSingleton<TradingHistoryStore>();
builder.Services.AddSingleton<PositionsStore>();
builder.Services.AddAuthentication(UserTokenAuthenticationOptions.SchemeName)
    .AddScheme<UserTokenAuthenticationOptions, UserTokenAuthenticationHandler>(
        UserTokenAuthenticationOptions.SchemeName,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new SafeDecimalConverter());
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
}

app.MapPost("/api/auth/register", async (HttpContext context, AuthRequest request, UserRegistryService registry) =>
{
    var deviceId = GetDeviceId(context);
    var (success, error, response, userId) = await registry.RegisterAsync(request.UserName, request.Password, deviceId);
    if (!success || response is null || string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(response);
});

app.MapPost("/api/auth/login", async (HttpContext context, AuthRequest request, UserRegistryService registry) =>
{
    var deviceId = GetDeviceId(context);
    var (success, error, response) = await registry.LoginAsync(request.UserName, request.Password, deviceId);
    return success && response is not null
        ? Results.Ok(response)
        : Results.BadRequest(new { error });
});

app.MapPost("/api/auth/logout", async (HttpContext context, UserRegistryService registry) =>
{
    var token = GetToken(context);
    var success = await registry.LogoutAsync(token);
    return success ? Results.Ok() : Results.BadRequest();
});

app.MapGet("/api/auth/me", async (HttpContext context, UserRegistryService registry) =>
{
    var user = await AuthenticateAsync(context, registry);
    return user is null ? Results.Unauthorized() : Results.Ok(new { user.UserName });
});


app.UseBlazorFrameworkFiles();   // serve _framework from Client build
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("index.html");

app.Run();

static string? GetToken(HttpContext context)
{
    if (!context.Request.Headers.TryGetValue("X-User-Token", out var values))
    {
        return null;
    }

    return values.FirstOrDefault();
}

static string? GetDeviceId(HttpContext context)
{
    if (!context.Request.Headers.TryGetValue("X-User-Device-Id", out var values))
    {
        return null;
    }

    return values.FirstOrDefault();
}

static async Task<UserRecord?> AuthenticateAsync(HttpContext context, UserRegistryService registry)
{
    var token = GetToken(context);
    return await registry.GetUserByTokenAsync(token);
}


