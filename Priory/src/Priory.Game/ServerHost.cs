using System.Collections.Concurrent;
using System.Text;

namespace Priory.Game;

public static class ServerHost
{
    public static Task RunAsync(string[] args, StoryData story, string saveRoot, SaveCodec saveCodec)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
        });

        var app = builder.Build();
        app.UseCors();
        app.Use(async (context, next) =>
        {
            await next();
            if (context.Request.Method != HttpMethods.Get) return;

            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                ctx.Context.Response.Headers.Pragma = "no-cache";
                ctx.Context.Response.Headers.Expires = "0";
            }
        });

        var sessions = new ConcurrentDictionary<string, SessionState>(StringComparer.OrdinalIgnoreCase);
        var accountRepo = new AccountRepository(saveRoot);

        var buildId = Environment.GetEnvironmentVariable("PRIORY_BUILD_ID") ?? "dev";
        var startedAtUtc = DateTimeOffset.UtcNow;

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/version", () => Results.Ok(new { buildId, startedAtUtc }));

        app.MapPost("/api/sessions", (CreateSessionRequest request) =>
        {
            var engine = new GameEngine(story, saveRoot, saveCodec);
            var bootstrapLines = new List<string>();
            string? partyCode = null;
            var authMode = (request.AuthMode ?? "guest").Trim().ToLowerInvariant();
            var hasCredentials = !string.IsNullOrWhiteSpace(request.Username) || !string.IsNullOrWhiteSpace(request.Password);

            if (hasCredentials || authMode is "login" or "register")
            {
                if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                    return Results.BadRequest(new { error = "username and password are required for login/register" });

                if (authMode == "register")
                {
                    if (!accountRepo.Register(request.Username, request.Password, request.PlayerName, out var registerMessage))
                        return Results.BadRequest(new { error = registerMessage });
                    bootstrapLines.Add(registerMessage);
                }
                else
                {
                    if (!accountRepo.TryAuthenticate(request.Username, request.Password, out var account, out var authMessage))
                        return Results.BadRequest(new { error = authMessage });
                    bootstrapLines.Add($"Welcome back, {account?.DisplayName ?? request.Username.Trim()}.");
                }
            }

            if (!string.IsNullOrWhiteSpace(request.ResumeCode))
            {
                var resumed = engine.TryResume(request.ResumeCode, out var message);
                bootstrapLines.Add(message);
                if (!resumed)
                    return Results.BadRequest(new { error = message });
            }
            else
            {
                switch ((request.Mode ?? "solo").Trim().ToLowerInvariant())
                {
                    case "create":
                        partyCode = engine.CreateParty();
                        bootstrapLines.Add($"Party created: {partyCode}");
                        break;
                    case "join":
                        if (string.IsNullOrWhiteSpace(request.PartyCode))
                            return Results.BadRequest(new { error = "partyCode is required when mode=join" });
                        if (!engine.JoinParty(request.PartyCode, out var joinMessage))
                            return Results.BadRequest(new { error = joinMessage });
                        bootstrapLines.Add(joinMessage);
                        break;
                    default:
                        engine.UseSoloMode();
                        break;
                }

                if (string.IsNullOrWhiteSpace(request.PlayerName))
                    return Results.BadRequest(new { error = "playerName is required for new games" });

                bootstrapLines.AddRange(CaptureConsoleLines(() => engine.StartNewGame(request.PlayerName)));
            }

            var sessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
            sessions[sessionId] = new SessionState(engine);

            return Results.Ok(new
            {
                sessionId,
                bootstrapLines,
                partyCode,
                isPartyMode = engine.IsPartyMode,
                activePartyCode = engine.ActivePartyCode,
                username = request.Username?.Trim()
            });
        });

        app.MapPost("/api/sessions/{id}/command", (string id, CommandRequest request) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            var output = session.Engine.HandleInput(request.Input ?? string.Empty);
            session.LastTimedPrompt = output.TimedPrompt;
            return Results.Ok(new
            {
                output.Lines,
                timedPrompt = output.TimedPrompt
            });
        });

        app.MapPost("/api/sessions/{id}/timed", (string id, TimedRequest request) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            var output = session.Engine.ResolveTimed(request.Choice);
            session.LastTimedPrompt = output.TimedPrompt;
            return Results.Ok(new
            {
                output.Lines,
                timedPrompt = output.TimedPrompt
            });
        });

        app.MapGet("/api/sessions/{id}/overview", (string id) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            var overview = session.Engine.GetPlayerOverview();
            return Results.Ok(new
            {
                overview.PlayerName,
                overview.LifePath,
                overview.Inventory,
                overview.Coin
            });
        });

        app.MapDelete("/api/sessions/{id}", (string id) =>
        {
            sessions.TryRemove(id, out _);
            return Results.NoContent();
        });

        return app.RunAsync();
    }

    private static List<string> CaptureConsoleLines(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();
    }

    private sealed class SessionState(GameEngine engine)
    {
        public GameEngine Engine { get; } = engine;
        public TimedPrompt? LastTimedPrompt { get; set; }
    }
}

public sealed class CreateSessionRequest
{
    public string? Mode { get; set; }
    public string? PlayerName { get; set; }
    public string? PartyCode { get; set; }
    public string? ResumeCode { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? AuthMode { get; set; }
}

public sealed class CommandRequest
{
    public string? Input { get; set; }
}

public sealed class TimedRequest
{
    public int Choice { get; set; }
}
