using ScidServer.Api;
using ScidServer.Configuration;
using ScidServer.Infrastructure;
using ScidServer.Realtime;
using ScidServer.Security;
using ScidServer.State;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var keyPath = Path.IsPathRooted(options.KeyPath)
    ? options.KeyPath
    : Path.Combine(builder.Environment.ContentRootPath, options.KeyPath);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TokenService.LoadOrCreate(keyPath));
builder.Services.AddSingleton<MemoryStore>();
builder.Services.AddSingleton<PinService>();
builder.Services.AddSingleton<PushHub>();
builder.Services.AddSingleton<ScidJson>();
builder.Services.AddSingleton<ScidApi>();
builder.Services.AddHttpClient("cdn", client => client.Timeout = TimeSpan.FromSeconds(15));

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = null);

var app = builder.Build();

app.UseWebSockets();
app.UseMiddleware<RequestLogging>();
app.Services.GetRequiredService<ScidApi>().Map(app);

app.Logger.LogInformation("Signing key {KeyId} from {KeyPath}", app.Services.GetRequiredService<TokenService>().KeyId, keyPath);
if (options.EnableDevEndpoints)
    app.Logger.LogInformation("Dev endpoints enabled: {Endpoints}",
        "POST /dev/token, POST /dev/items, POST /dev/accounts/{id}/revoke, GET /dev/state");

app.Run();
