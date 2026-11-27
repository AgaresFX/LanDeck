using PadNook.Core;
using PadNook.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace PadNook.Web;

/// <summary>Kestrel host: serves the embedded webapp and the WebSocket endpoint.</summary>
public sealed class WebServer(WsHub hub, SecurityStore security, ImageStore images, int port)
{
    private WebApplication? _app;

    public int Port => port;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "PadNook",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Testing only: PADNOOK_DEV_LOCALONLY=1 listens on localhost, so a test exe in a new folder does not
            // make Windows Firewall ask for (or silently add) a rule for that path.
            if (Environment.GetEnvironmentVariable("PADNOOK_DEV_LOCALONLY") == "1") k.ListenLocalhost(port);
            else k.ListenAnyIP(port);
            k.AddServerHeader = false;
        });

        var app = builder.Build();

        // LAN only: drop anything that does not come from a private address.
        app.Use(async (ctx, next) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null || !LanFilter.IsLocal(ip))
            {
                ctx.Abort();
                return;
            }
            await next();
        });

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        var files = CreateFileProvider();
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = c =>
            {
                var path = c.Context.Request.Path.Value ?? "";
                c.Context.Response.Headers.CacheControl = path.StartsWith("/assets/", StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable" // hashed file names
                    : "no-cache";
            },
        });

        RequestDelegate ws = hub.HandleAsync;
        app.Map("/ws", ws);
        app.MapGet("/api/ping", () => Results.Text($"PadNook {Program.Version}"));
        app.MapPost("/api/images", UploadImageAsync);
        app.MapGet("/api/imagesearch", SearchImagesAsync);
        app.MapGet("/api/imagefetch", FetchImageAsync);
        app.MapGet("/api/programicon", ProgramIconAsync);
        app.MapGet("/images/{id}", (HttpContext ctx, string id) =>
        {
            var path = images.PathFor(id);
            if (path is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable"; // content-addressed
            return Results.File(path, ImageStore.ContentType(id));
        });

        await app.StartAsync();
        _app = app;
        Log.Info($"Server listening on port {port}");
    }

    // A (HttpContext => Task) handler binds as RequestDelegate, so the IResult must be executed explicitly.
    private async Task UploadImageAsync(HttpContext ctx) => await (await ProcessUploadAsync(ctx)).ExecuteAsync(ctx);

    /// <summary>Editor requests: the desktop editor (loopback + key) or a paired device with an unlocked edit session.</summary>
    private IResult? CheckEditAuth(HttpContext ctx)
    {
        var desktopKey = ctx.Request.Headers["X-Desktop-Key"].ToString();
        var desktop = desktopKey.Length > 0
                      && DesktopEditorAuth.Matches(desktopKey)
                      && ctx.Connection.RemoteIpAddress is { } rip && System.Net.IPAddress.IsLoopback(rip);
        if (!desktop)
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null;
            var device = token is null ? null : security.Authenticate(token);
            if (device is null) return Results.Text(L.T("Dispositivo non associato"), statusCode: 401);
            if (!hub.IsEditTokenValid(ctx.Request.Headers["X-Edit-Token"].ToString(), device.Id))
                return Results.Text(L.T("Modalità modifica bloccata"), statusCode: 403);
        }
        return null;
    }

    private async Task SearchImagesAsync(HttpContext ctx)
    {
        var result = CheckEditAuth(ctx);
        if (result is null)
        {
            var q = ctx.Request.Query["q"].ToString().Trim();
            if (q.Length is 0 or > 100) result = Results.Text(L.T("Scrivi cosa cercare"), statusCode: 400);
            else
            {
                try
                {
                    var hits = ctx.Request.Query["kind"] == "photos"
                        ? await ImageSearch.SearchPhotosAsync(q, ctx.RequestAborted)
                        : await ImageSearch.SearchIconsAsync(q, ctx.RequestAborted);
                    result = Results.Text(ImageSearch.ToJson(hits), "application/json");
                }
                catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
                {
                    Log.Info($"Image search failed: {ex.Message}");
                    result = Results.Text(L.T("Ricerca non riuscita: controlla la connessione a internet"), statusCode: 502);
                }
            }
        }
        await result.ExecuteAsync(ctx);
    }

    /// <summary>Downloads a search result for the editor, which then resizes and uploads it.</summary>
    private async Task FetchImageAsync(HttpContext ctx)
    {
        var result = CheckEditAuth(ctx);
        if (result is null)
        {
            try
            {
                var (data, type) = await ImageSearch.FetchAsync(ctx.Request.Query["url"].ToString(), ctx.RequestAborted);
                result = Results.Bytes(data, type);
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                Log.Info($"Image download failed: {ex.Message}");
                result = Results.Text(L.F("Download non riuscito: {0}", ex.Message), statusCode: 502);
            }
        }
        await result.ExecuteAsync(ctx);
    }

    /// <summary>Image upload: requires a paired device token and an unlocked edit session.</summary>
    /// <summary>Saves the real icon of a program/shortcut on the PC as an image and returns its id.</summary>
    private async Task ProgramIconAsync(HttpContext ctx)
    {
        var result = CheckEditAuth(ctx);
        if (result is null)
        {
            var path = ResolveProgramPath(ctx.Request.Query["path"].ToString());
            var png = path is null ? null : await Platform.ProgramIcon.GetPngAsync(path);
            var id = png is null ? null : images.Save(png);
            result = id is null
                ? Results.Text(L.T("Icona non trovata"), statusCode: 404)
                : Results.Text($"{{\"id\":\"{id}\"}}", "application/json");
        }
        await result.ExecuteAsync(ctx);
    }

    /// <summary>Expands variables and finds bare names (e.g. «notepad.exe») on the PATH, like the launch action does.</summary>
    private static string? ResolveProgramPath(string raw)
    {
        var target = Environment.ExpandEnvironmentVariables(raw).Trim().Trim('"');
        if (target.Length is 0 or > 1024 || target.Contains("://")) return null;
        if (Path.IsPathFullyQualified(target)) return File.Exists(target) || Directory.Exists(target) ? target : null;
        if (target.IndexOfAny(['\\', '/']) >= 0) return null;
        var names = Path.HasExtension(target) ? [target] : new[] { target + ".exe", target };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(Environment.SystemDirectory).Prepend(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        foreach (var dir in dirs)
            foreach (var name in names)
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), name);
                    if (File.Exists(full)) return full;
                }
                catch { /* bad PATH entry */ }
            }
        return null;
    }

    private async Task<IResult> ProcessUploadAsync(HttpContext ctx)
    {
        if (CheckEditAuth(ctx) is { } denied) return denied;
        if (ctx.Request.ContentLength > ImageStore.MaxBytes)
            return Results.Text(L.T("Immagine troppo grande (max 8 MB)"), statusCode: 413);

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > ImageStore.MaxBytes)
                return Results.Text(L.T("Immagine troppo grande (max 8 MB)"), statusCode: 413);
        }

        var id = images.Save(ms.ToArray());
        return id is null
            ? Results.Text(L.T("Formato non supportato: usa PNG, JPEG, GIF o WebP"), statusCode: 415)
            : Results.Text($"{{\"id\":\"{id}\"}}", "application/json");
    }

    private static IFileProvider CreateFileProvider()
    {
        try
        {
            return new ManifestEmbeddedFileProvider(typeof(WebServer).Assembly, "wwwroot");
        }
        catch (Exception ex)
        {
            Log.Error("Embedded webapp not found", ex);
            return new NullFileProvider();
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
        }
        catch (Exception ex) { Log.Error("Error stopping server", ex); }
    }
}
