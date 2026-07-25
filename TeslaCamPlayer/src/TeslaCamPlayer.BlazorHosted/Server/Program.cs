using Serilog;
using Serilog.Events;
using TeslaCamPlayer.BlazorHosted.Server.Hubs;
using TeslaCamPlayer.BlazorHosted.Server.Providers;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services;
using TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(LogEventLevel.Verbose)
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews().AddNewtonsoftJson();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<ISettingsProvider, SettingsProvider>();
builder.Services.AddSingleton<IRefreshProgressService, RefreshProgressService>();
builder.Services.AddSingleton<IClipIndexRepository, SqliteClipIndexRepository>();
builder.Services.AddSingleton<IClipsService, ClipsService>();
builder.Services.AddSingleton<IExportService, ExportService>();
builder.Services.AddSingleton<IFfmpegCapabilities, FfmpegCapabilitiesService>();
builder.Services.AddSingleton<ISeiParserService, SeiParserService>();
builder.Services.AddTransient<IMp4TimingService, Mp4TimingService>();
builder.Services.AddHostedService<ExportCleanupService>();

// Encrypted TeslaCam clip decryption (Tesla dashcam account auth + on-demand decrypt).
builder.Services.AddHttpClient("tesla", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");
});
// Update notifications: GitHub requires a User-Agent on API requests.
builder.Services.AddHttpClient("github", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TeslaCamPlayer");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddSingleton<IUpdateCheckService, UpdateCheckService>();

builder.Services.AddSingleton<ITeslaAuthService, TeslaAuthService>();
builder.Services.AddSingleton<ITeslaKeyService, TeslaKeyService>();
builder.Services.AddSingleton<IClipDecryptionService, ClipDecryptionService>();
builder.Services.AddHostedService<DecryptedCacheCleanupService>();

builder.Services.AddSignalR();
// HudRendererService picks its python and script paths itself (including a runtime OS check), so it
// registers the same way everywhere. Only ffprobe differs, and only Windows-vs-not: the Docker and
// Linux implementations are the same bare "ffprobe" on PATH.
builder.Services.AddSingleton<IHudRendererService, HudRendererService>();
#if WINDOWS
builder.Services.AddSingleton<FfProbeService, FfProbeServiceWindows>();
#else
builder.Services.AddSingleton<FfProbeService, FfProbeServiceLinux>();
#endif
builder.Services.AddTransient<IFfProbeService>(sp =>
    new HybridDurationProbeService(sp.GetRequiredService<FfProbeService>()));

var app = builder.Build();

var clipsRootPath = app.Services.GetService<ISettingsProvider>()!.Settings.ClipsRootPath;
try
{
    if (string.IsNullOrWhiteSpace(clipsRootPath) || !Directory.Exists(clipsRootPath))
    {
        Log.Warning("Configured clips root path doesn't exist, or no permission to access: {ClipsRootPath}. The WebUI settings dialog will prompt for configuration.", clipsRootPath);
    }
}
catch (Exception e)
{
    Log.Warning(e, "Configured clips root path could not be checked. The WebUI settings dialog will prompt for configuration.");
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseBlazorFrameworkFiles();

// .proto is not in the default MIME map; without this the client's fetch of dashcam.proto 404s.
var staticContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticContentTypeProvider.Mappings[".proto"] = "text/plain";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypeProvider });

app.UseRouting();


app.MapRazorPages();
app.MapControllers();
app.MapHub<StatusHub>("/hubs/status");
app.MapFallbackToFile("index.html");

app.Run();
