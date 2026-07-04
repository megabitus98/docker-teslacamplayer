using Serilog;
using Serilog.Events;
using TeslaCamPlayer.BlazorHosted.Server.Hubs;
using TeslaCamPlayer.BlazorHosted.Server.Providers;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services;
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
builder.Services.AddTransient<IClipsService, ClipsService>();
builder.Services.AddSingleton<IExportService, ExportService>();
builder.Services.AddSingleton<ISeiParserService, SeiParserService>();
builder.Services.AddTransient<IMp4TimingService, Mp4TimingService>();
builder.Services.AddHostedService<ExportCleanupService>();
builder.Services.AddSignalR();
#if WINDOWS
builder.Services.AddSingleton<FfProbeServiceWindows>();
builder.Services.AddTransient<IFfProbeService>(sp =>
    new HybridDurationProbeService(sp.GetRequiredService<FfProbeServiceWindows>()));
builder.Services.AddSingleton<IHudRendererService, HudRendererService>();
#elif DOCKER
builder.Services.AddSingleton<FfProbeServiceDocker>();
builder.Services.AddTransient<IFfProbeService>(sp =>
    new HybridDurationProbeService(sp.GetRequiredService<FfProbeServiceDocker>()));
builder.Services.AddSingleton<IHudRendererService, HudRendererService>();
#elif LINUX
builder.Services.AddSingleton<FfProbeServiceLinux>();
builder.Services.AddTransient<IFfProbeService>(sp =>
    new HybridDurationProbeService(sp.GetRequiredService<FfProbeServiceLinux>()));
builder.Services.AddSingleton<IHudRendererService, HudRendererService>();
#endif

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
app.UseStaticFiles();

app.UseRouting();


app.MapRazorPages();
app.MapControllers();
app.MapHub<StatusHub>("/hubs/status");
app.MapFallbackToFile("index.html");

app.Run();
