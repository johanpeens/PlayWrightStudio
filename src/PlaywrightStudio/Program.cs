using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;
using PlaywrightStudio.Components;
using PlaywrightStudio.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddSingleton<StudioPaths>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ScenarioStore>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<PlaywrightHost>();
builder.Services.AddSingleton<RecorderService>();
builder.Services.AddSingleton<FileSystemBrowser>();
builder.Services.AddSingleton<VideoConverter>();
builder.Services.AddSingleton<RunnerService>();
builder.Services.AddSingleton<CodeExporter>();
builder.Services.AddSingleton<ManualStore>();
builder.Services.AddSingleton<ManualService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();

// Screenshots, videos and traces live outside wwwroot so a rebuild never deletes them.
var paths = app.Services.GetRequiredService<StudioPaths>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(paths.Runs),
    RequestPath = "/runmedia",
    ServeUnknownFileTypes = true
});

// Pictures belonging to written manuals.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(paths.Manuals),
    RequestPath = "/manualmedia",
    ServeUnknownFileTypes = true
});

// Run videos live next to the app.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(paths.Videos),
    RequestPath = "/videos",
    ServeUnknownFileTypes = true
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStarted.Register(() => DemoSeeder.SeedIfEmpty(app.Services));

// Downloading a generated manual pdf.
app.MapGet("/download/manual/{manualId}/{**file}", (string manualId, string file, StudioPaths p) =>
{
    var full = Path.GetFullPath(Path.Combine(p.Manuals, manualId, file));
    if (!full.StartsWith(Path.GetFullPath(p.Manuals), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        return Results.NotFound();
    return Results.File(full, "application/pdf", Path.GetFileName(full));
});

// Downloading an exported test file or a trace zip.
app.MapGet("/download/run/{runId}/{**file}", (string runId, string file, StudioPaths p) =>
{
    var full = Path.GetFullPath(Path.Combine(p.Runs, runId, file));
    if (!full.StartsWith(Path.GetFullPath(p.Runs), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        return Results.NotFound();
    return Results.File(full, "application/octet-stream", Path.GetFileName(full));
});

app.Run();
