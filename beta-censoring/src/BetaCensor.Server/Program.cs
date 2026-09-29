using BetaCensor.Caching;
using BetaCensor.Core.Messaging;
using BetaCensor.Server;
using BetaCensor.Server.Discovery;
using BetaCensor.Web;
using BetaCensor.Web.Status;
using BetaCensor.Workers;
using CensorCore;
using CensorCore.ModelLoader;
using ConfigurEngine;
using MediatR;
using Microsoft.AspNetCore.Http.Json;
using System.Runtime.InteropServices;

// a Windows service starts in C:\Windows\System32: read config.yml (and relative paths in it) from the server's folder
if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()) {
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
}

var builder = WebApplication.CreateBuilder(args);
// builder.WebHost.AdvertiseServer();

builder.Configuration
    .AddConfigFile("config")
    .AddConfigFile("stickers")
    .AddConfigFile("beta-config");
    
builder.Configuration
    .AddEnvironmentVariables("BCS_")
    .AddCommandLine(args);

var serverOpts = builder.Configuration.GetServerOptions() ?? new ServerOptions();
var listenUrl = serverOpts.GetListenUrl();
builder.WebHost.UseUrls(listenUrl);
if (!serverOpts.IsLocalOnly()) {
    Console.WriteLine($"WARN: listening on {listenUrl}: anyone who can reach this port can use the server, it has no authentication"
        + " (in Docker, the published ports decide who can reach it)");
}

builder.Host.UseSystemd();
builder.Host.UseWindowsService();

builder.WebHost.UseConfiguration(builder.Configuration);

//TODO: while effective (and clean), this is kind of a bad hack
// we should add a runtimeSettings.json, use the csproj to include it as an embedded resource
// then use AddJsonStream to pull it back out. Then it's still a JSON config like appSettings
// but we don't have to include a loose JSON file.
builder.Logging.AddFilter("Microsoft.AspNetCore", level => level > LogLevel.Warning);

// Add services to the container.

var loader = new ModelLoaderBuilder()
        .AddDefaultPaths()
        .SearchAssembly(System.Reflection.Assembly.GetEntryAssembly())
        .Build();
// ModelPath can point at a specific .onnx file or a folder (e.g. BCS_ModelPath=/models/640m.onnx)
var modelPath = builder.Configuration["ModelPath"];
if (!string.IsNullOrWhiteSpace(modelPath) && !File.Exists(modelPath) && !Directory.Exists(modelPath)) {
    Console.WriteLine($"WARN: ModelPath '{modelPath}' does not exist, searching default locations instead");
}
var modelHelp = $"Download {RepositoryDownloadClient.DefaultModelName} (or the larger 640m.onnx) from {RepositoryDownloadClient.DefaultModelPage}, "
    + "put it in the server's folder, or set ModelPath to its location.";
byte[]? model = null;
try {
    model = await loader.GetModel(modelPath);
} catch (Exception e) {
    Console.Error.WriteLine($"ERROR: no model file was found, and downloading one failed: {e.Message}");
}
if (model == null) {
    Console.Error.WriteLine($"ERROR: could not get the NudeNet model. {modelHelp}");
    Environment.Exit(1);
}
Console.WriteLine($"Using the model from {loader.Source}");

builder.Services.AddCensoring(model, serverOpts.UseGpu, serverOpts.GpuDeviceId, serverOpts.GpuMemoryLimitMB * 1024L * 1024L, serverOpts.GpuMaxConcurrentRuns);
builder.Services.AddSingleton<CensorCore.Censoring.ICensoringMiddleware, BetaCensor.Core.ObfuscationMiddleware>();

builder.Services.AddSingleton<IImageHandler>(ServerConfigurationExtensions.BuildImageHandler(serverOpts));

// builder.Services.AddSpaStaticFiles(options => {options.RootPath = "wwwroot";});
var mvc = builder.Services.AddControllers(mvc => mvc.AddYamlFormatter()).AddJsonOptions(json => json.JsonSerializerOptions.ConfigureJsonOptions());
if (serverOpts.EnableRest) {
    Console.WriteLine("Enabling REST interface!");
    mvc.AddApplicationPart(typeof(CensorCore.Web.CensoringController).Assembly);
}
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.Services.AddStatusPages<ServerInfoService>(builder.Environment);

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(
    typeof(Program).Assembly,
    typeof(AIService).Assembly,
    typeof(BetaCensor.Core.Messaging.CensorImageRequest).Assembly,
    typeof(BetaCensor.Web.Controllers.InfoController).Assembly
));

builder.Services.AddPerformanceData();

if (serverOpts.EnableSignalR) {
    Console.WriteLine("Enabling SignalR interface!");
    builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = true;
        o.KeepAliveInterval = TimeSpan.FromSeconds(10);
        o.ClientTimeoutInterval = TimeSpan.FromMinutes(1);
    })
    .AddJsonProtocol(options => options.PayloadSerializerOptions.ConfigureJsonOptions())
    .AddHubOptions<BetaCensor.Server.Controllers.CensoringHub>(o =>
    {
        o.MaximumParallelInvocationsPerClient = 8;
        o.MaximumReceiveMessageSize = serverOpts.EnableLargeMessages ? null : 33554432;
    });
}

if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !string.IsNullOrWhiteSpace(serverOpts.SocketPath)) {
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenUnixSocket(serverOpts.SocketPath);
    });
}

builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.ConfigureJsonOptions());

builder.Services.AddQueues<CensorImageRequest, CensorImageResponse>();
builder.Services.AddDefaultManagedRequestQueue();
// we don't want to validate this at the queue level because it would get *silently* dropped, not rejected.
// if it happens anywhere, it should be in the individual APIs before being queued.
// builder.Services.AddDefaultManagedRequestQueue((req => req.ImageUrl, url => (url ?? string.Empty).EndsWith(".gif")));
builder.Services.AddWorkers<DispatchWorkerService<CensorImageRequest, CensorImageResponse>>(builder.Configuration.GetSection("Server"));
builder.Services.AddWorkers<DispatchNotificationService<CensorImageResponse>>(1);

if (serverOpts.EnableDiscovery) {
    builder.Services.AddHostedService<DiscoveryService>();
}

// var stickerOpts = builder.Configuration.GetSection("Stickers");
// var captionsOpts = builder.Configuration.GetSection("Captions");
builder.Services.AddStickerService(builder.Environment);

builder.Services.AddScoped<MatchOptions>(ServerConfigurationExtensions.BuildMatchOptions);
builder.Services.AddSingleton<CensorCore.Censoring.GlobalCensorOptions>(ServerConfigurationExtensions.BuildCensorOptions);

builder.Services.EnableCaching(cache => cache.AddHandler().AddOptions());


var app = builder.Build();

// load the model now rather than at the first request, so a broken model file shows up at once
try {
    app.Services.GetRequiredService<AIService>();
} catch (Exception e) {
    Console.Error.WriteLine($"ERROR: could not load the model from {loader.Source}: {e.Message}");
    Console.Error.WriteLine($"The file may be incomplete or not an ONNX model. {modelHelp}");
    Environment.Exit(1);
}

var originPolicy = new RequestOriginPolicy(serverOpts.AllowedOrigins, serverOpts.AllowedHosts, serverOpts.AllowedExtensions);
app.Use(async (context, next) => {
    var headers = context.Request.Headers;
    var origin = headers.Origin.ToString();
    var host = context.Request.Host.Value ?? string.Empty;
    if (!originPolicy.IsAllowed(origin, context.Request.Scheme, host, headers["Sec-Fetch-Site"].ToString(), headers.Referer.ToString())) {
        if (!originPolicy.IsKnownHost(host)) {
            app.Logger.LogWarning("Refused a request addressed to {Host}; add the name to Server:AllowedHosts to allow it", host);
        } else {
            app.Logger.LogWarning("Refused a request from the web page {Origin}; add it to Server:AllowedOrigins (or the extension's id to Server:AllowedExtensions) to allow it",
                string.IsNullOrEmpty(origin) ? headers.Referer.ToString() : origin);
        }
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

// if (app.Environment.IsDevelopment()) {
app.UseSwagger();
app.UseSwaggerUI();
// }

app.UseRouting();
// app.UseAuthorization();
app.UseEndpoints(e =>
{
    if (serverOpts.EnableSignalR) {
        e.MapHub<BetaCensor.Server.Controllers.CensoringHub>("/live", conf => {
            conf.ApplicationMaxBufferSize = 33554432;
            conf.TransportMaxBufferSize = 33554432;
        });
    }
});
// app.MapHub<BetaCensor.Server.Controllers.CensoringHub>("/live");

app.MapControllers();
app.UseWebSockets();
app.UseStatusPages(app.Environment);
app.UseStickerProvider();
app.Run();
