using BlazorOptions.Server.Models;
using BlazorOptions.Server.Options;
using BlazorOptions.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.Configure<DataStorageOptions>(
    builder.Configuration.GetSection(DataStorageOptions.SectionName));
builder.Services.AddSingleton<UserRegistryService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
}

app.MapPost("/api/auth/register", async (AuthRequest request, UserRegistryService registry) =>
{
    var (success, error, response, userId) = await registry.RegisterAsync(request.UserName, request.Password);
    if (!success || response is null || string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(response);
});

app.MapPost("/api/auth/login", async (AuthRequest request, UserRegistryService registry) =>
{
    var (success, error, response) = await registry.LoginAsync(request.UserName, request.Password);
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




app.UseBlazorFrameworkFiles();   // serve _framework from Client build
app.UseStaticFiles();

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

static async Task<UserRecord?> AuthenticateAsync(HttpContext context, UserRegistryService registry)
{
    var token = GetToken(context);
    return await registry.GetUserByTokenAsync(token);
}


