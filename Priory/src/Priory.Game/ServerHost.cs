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
            var requestedUsername = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();

            var mode = (request.Mode ?? "solo").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(request.ResumeCode) && mode == "join" && string.IsNullOrWhiteSpace(request.PartyCode))
                return Results.BadRequest(new { error = "Please insert party code or change to solo world type." });

            if (!string.IsNullOrWhiteSpace(request.ResumeCode))
            {
                var resumed = engine.TryResume(request.ResumeCode, out var message);
                bootstrapLines.Add(message);
                if (!resumed)
                    return Results.BadRequest(new { error = message });
            }
            else
            {
                switch (mode)
                {
                    case "create":
                        partyCode = engine.CreateParty();
                        bootstrapLines.Add($"Party created: {partyCode}");
                        break;
                    case "join":
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

                if (authMode is "login" or "register")
                {
                    if (string.IsNullOrWhiteSpace(requestedUsername) || string.IsNullOrWhiteSpace(request.Password))
                        return Results.BadRequest(new { error = "username and password are required for login/register" });

                    if (authMode == "register")
                    {
                        if (!accountRepo.Register(requestedUsername, request.Password, request.PlayerName, out var registerMessage))
                            return Results.BadRequest(new { error = registerMessage });
                        bootstrapLines.Add(registerMessage);
                    }
                    else
                    {
                        if (!accountRepo.TryAuthenticate(requestedUsername, request.Password, out var account, out var authMessage))
                            return Results.BadRequest(new { error = authMessage });
                        bootstrapLines.Add($"Welcome back, {account?.DisplayName ?? requestedUsername}.");

                        if (accountRepo.TryGetAccount(requestedUsername, out var existing) && !string.IsNullOrWhiteSpace(existing?.LastSaveCode))
                        {
                            var resumed = engine.TryResume(existing.LastSaveCode!, out var autoResumeMessage);
                            if (resumed)
                            {
                                bootstrapLines.Add("Loaded your most recent account save.");
                                bootstrapLines.Add(autoResumeMessage);
                            }
                        }
                    }
                }

                if (engine.GetPlayerOverview().PlayerName == "Pilgrim" && engine.GetPlayerOverview().LifePath is null)
                    bootstrapLines.AddRange(CaptureConsoleLines(() => engine.StartNewGame(request.PlayerName, ParseSex(request.Sex))));
            }

            var sessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
            sessions[sessionId] = new SessionState(engine, requestedUsername, authMode);

            return Results.Ok(new
            {
                sessionId,
                bootstrapLines,
                partyCode,
                isPartyMode = engine.IsPartyMode,
                activePartyCode = engine.ActivePartyCode,
                username = requestedUsername
            });
        });

        app.MapPost("/api/sessions/{id}/command", (string id, CommandRequest request) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            var input = request.Input ?? string.Empty;
            var output = session.Engine.HandleInput(input);
            var lines = output.Lines;

            if (string.Equals(input.Trim(), "save", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(session.Username) && (session.AuthMode is "login" or "register"))
                {
                    var codeLine = lines.FirstOrDefault(x => x.StartsWith("SAVE CODE:", StringComparison.OrdinalIgnoreCase));
                    if (codeLine is not null)
                    {
                        var code = codeLine.Split('|', 2)[0].Replace("SAVE CODE:", "", StringComparison.OrdinalIgnoreCase).Trim();
                        accountRepo.UpdateLastSaveCode(session.Username, code);
                        lines = new List<string> { "Progress saved to your account." };
                    }
                }
                else
                {
                    lines = new List<string>
                    {
                        "Guest sessions are temporary and are not linked to an account.",
                        "To keep progress, return to startup and register/login with a password."
                    };
                }
            }

            session.LastTimedPrompt = output.TimedPrompt;
            return Results.Ok(new
            {
                lines,
                timedPrompt = output.TimedPrompt,
                quitToMenu = lines.Any(x => x.Contains("Leaving game.", StringComparison.OrdinalIgnoreCase))
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

    private static PlayerSex ParseSex(string? sex)
    {
        return sex?.Trim().ToLowerInvariant() switch
        {
            "female" or "f" => PlayerSex.Female,
            _ => PlayerSex.Male
        };
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

    private sealed class SessionState(GameEngine engine, string? username, string authMode)
    {
        public GameEngine Engine { get; } = engine;
        public string? Username { get; } = username;
        public string AuthMode { get; } = authMode;
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
    public string? Sex { get; set; }
}

public sealed class CommandRequest
{
    public string? Input { get; set; }
}

public sealed class TimedRequest
{
    public int Choice { get; set; }
}
