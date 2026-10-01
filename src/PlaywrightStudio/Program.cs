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
builder.Services.AddSingleton<RecorderService>();
builder.Services.AddSingleton<FileSystemBrowser>();
builder.Services.AddSingleton<CodeExporter>();
builder.Services.AddSingleton<ManualStore>();
builder.Services.AddSingleton<ManualService>();
builder.Services.AddSingleton<PageSessions>();
builder.Services.AddSingleton<StudioScript>();
builder.Services.AddScoped<PageRunner>();
builder.Services.AddScoped<ActiveScenario>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// The page under test talks back over a socket. This is the only channel the studio needs -
// it launches no browser and starts no process.
app.UseWebSockets();

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

// A manual, as a standalone printable page. Ctrl+P or the Print button turns it into a pdf
// using the browser the user already has - no server-side browser, no pdf library.
app.MapGet("/manual/{manualId}/print", (string manualId, ManualStore store, ManualService manuals) =>
{
    var manual = store.Get(manualId);
    if (manual is null) return Results.NotFound();

    var html = manuals.BuildHtml(manual);

    // Opening the print dialog on load is what makes this feel like a download rather than
    // a detour, and printing is the only reason to be on this page.
    html = html.Replace("</body>",
        "<script>window.addEventListener('load', () => setTimeout(() => window.print(), 300));</script></body>");

    return Results.Content(html, "text/html; charset=utf-8");
});

// ---------------------------------------------------------------- the page-side script

// One script tag in the app under test loads everything: transport, recorder, player, run bar.
app.MapGet("/pws.js", async (StudioScript script, HttpResponse response) =>
{
    response.ContentType = "application/javascript; charset=utf-8";
    // Any origin may load it - the page under test is usually not served by the studio.
    response.Headers["Access-Control-Allow-Origin"] = "*";
    response.Headers["Cache-Control"] = "no-cache";
    await response.WriteAsync(await script.BuildAsync());
});

// The socket every instrumented page holds open. Pages arrive on their own; the studio waits to
// be told which one to record with.
app.Map("/pws/socket", async (HttpContext context, PageSessions pages, ILoggerFactory loggers) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var id = context.Request.Query["id"].ToString();
    if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("n");

    var log = loggers.CreateLogger("PageSocket");
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var page = pages.Add(id, socket);

    var buffer = new byte[32 * 1024];
    var text = new System.Text.StringBuilder();

    try
    {
        while (socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;

            text.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count));
            if (!received.EndOfMessage) continue;      // a long payload arrives in pieces

            var json = text.ToString();
            text.Clear();

            // "hello" is the studio's own bookkeeping; everything else belongs to whoever
            // claimed this page.
            if (json.Contains("\"type\":\"hello\"", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    pages.Touch(page,
                        root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                        root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        root.TryGetProperty("token", out var k) ? k.GetString() : null);
                }
                catch (Exception ex) { log.LogDebug(ex, "Bad hello from {Id}", id); }
                continue;
            }

            page.Deliver(json);
        }
    }
    catch (OperationCanceledException) { /* the tab went away */ }
    catch (System.Net.WebSockets.WebSocketException) { /* likewise, and normal */ }
    finally
    {
        pages.Remove(id);
    }
});

app.Run();
