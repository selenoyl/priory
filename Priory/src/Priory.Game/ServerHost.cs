using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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

        var musicRoots = new[]
        {
            Path.Combine(app.Environment.ContentRootPath, "music"),
            Path.Combine(app.Environment.ContentRootPath, "wwwroot", "music"),
            Path.Combine(app.Environment.ContentRootPath, "data", "music")
        }
        .Where(Directory.Exists)
        .ToArray();

        if (musicRoots.Length > 0)
        {
            var providers = musicRoots.Select(root => (IFileProvider)new PhysicalFileProvider(root)).ToArray();
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = providers.Length == 1 ? providers[0] : new CompositeFileProvider(providers),
                RequestPath = "/music",
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    ctx.Context.Response.Headers.Pragma = "no-cache";
                    ctx.Context.Response.Headers.Expires = "0";
                }
            });
        }

        var sessions = new ConcurrentDictionary<string, SessionState>(StringComparer.OrdinalIgnoreCase);
        var accountRepo = new AccountRepository(saveRoot);
        var chatChannels = new ConcurrentDictionary<string, ChatChannelState>(StringComparer.OrdinalIgnoreCase);
        var chatStore = new ChatTranscriptStore(Path.Combine(saveRoot, "chat"));
        var chatBroadcaster = new ChatBroadcaster();

        var buildId = Environment.GetEnvironmentVariable("PRIORY_BUILD_ID") ?? "dev";
        var startedAtUtc = DateTimeOffset.UtcNow;

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/version", () => Results.Ok(new { buildId, startedAtUtc }));
        app.MapGet("/api/sessions/{id}/online-users", (string id) =>
        {
            if (!sessions.TryGetValue(id, out var current))
                return Results.NotFound(new { error = "session not found" });

            current.SyncPartyCodeFromEngine();
            var currentPartyCode = (current.LastKnownPartyCode ?? string.Empty).Trim().ToUpperInvariant();

            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);
            var activeSessions = sessions.Values
                .Where(x => x.LastActivityUtc >= cutoff)
                .ToArray();

            foreach (var session in activeSessions)
                session.SyncPartyCodeFromEngine();

            var activeNames = activeSessions
                .Select(x => x.Engine.GetPlayerOverview().PlayerName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var partyNames = string.IsNullOrWhiteSpace(currentPartyCode)
                ? Array.Empty<string>()
                : activeSessions
                    .Where(x => string.Equals((x.LastKnownPartyCode ?? string.Empty).Trim(), currentPartyCode, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Engine.GetPlayerOverview().PlayerName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            return Results.Ok(new
            {
                count = activeNames.Length,
                users = activeNames,
                partyCount = partyNames.Length,
                partyUsers = partyNames
            });
        });
        app.MapGet("/api/music-tracks", () =>
        {
            var tracks = musicRoots
                .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Where(path =>
                {
                    var ext = Path.GetExtension(path);
                    return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase);
                })
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(new { tracks });
        });

        app.MapPost("/api/sessions", (CreateSessionRequest request) =>
        {
            var engine = new GameEngine(story, saveRoot, saveCodec);
            var bootstrapLines = new List<string>();
            var resumedSession = false;
            string? partyCode = null;
            var authMode = (request.AuthMode ?? "guest").Trim().ToLowerInvariant();
            var requestedUsername = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
            var normalizedPlayerName = NormalizePlayerName(request.PlayerName);

            var mode = (request.Mode ?? "solo").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(request.ResumeCode) && mode == "join" && string.IsNullOrWhiteSpace(request.PartyCode))
                return Results.BadRequest(new { error = "Please insert party code or change to solo world type." });

            if (!string.IsNullOrWhiteSpace(request.ResumeCode))
            {
                var resumed = engine.TryResume(request.ResumeCode, out var message);
                bootstrapLines.Add(message);
                if (!resumed)
                    return Results.BadRequest(new { error = message });

                resumedSession = true;
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

                if (normalizedPlayerName is null)
                    return Results.BadRequest(new { error = "playerName is required for new games" });

                if (authMode is "login" or "register")
                {
                    if (string.IsNullOrWhiteSpace(requestedUsername) || string.IsNullOrWhiteSpace(request.Password))
                        return Results.BadRequest(new { error = "username and password are required for login/register" });

                    if (authMode == "register")
                    {
                        if (!accountRepo.Register(requestedUsername, request.Password, normalizedPlayerName, out var registerMessage))
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
                                resumedSession = true;
                            }
                        }
                    }
                }

                if (engine.GetPlayerOverview().PlayerName == "Pilgrim" && engine.GetPlayerOverview().LifePath is null)
                    bootstrapLines.AddRange(CaptureConsoleLines(() => engine.StartNewGame(normalizedPlayerName, ParseSex(request.Sex))));
            }

            if (resumedSession)
            {
                var lookResult = engine.HandleInput("look");
                bootstrapLines.AddRange(lookResult.Lines);
            }

            var sessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
            var createdSession = new SessionState(engine, requestedUsername, authMode);
            createdSession.Touch();
            createdSession.SyncPartyCodeFromEngine();
            sessions[sessionId] = createdSession;

            return Results.Ok(new
            {
                sessionId,
                bootstrapLines,
                partyCode,
                isPartyMode = engine.IsPartyMode,
                activePartyCode = engine.ActivePartyCode,
                username = requestedUsername,
                sceneId = engine.GetPlayerOverview().SceneId,
                totalQuestCount = story.Quests.Count
            });
        });

        app.MapPost("/api/sessions/{id}/command", (string id, CommandRequest request) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            session.Touch();
            var input = request.Input ?? string.Empty;
            var output = session.Engine.HandleInput(input);
            var lines = output.Lines;

            if (string.Equals(input.Trim(), "save", StringComparison.OrdinalIgnoreCase))
            {
                var codeLine = lines.FirstOrDefault(x => x.StartsWith("SAVE CODE:", StringComparison.OrdinalIgnoreCase));
                lines = lines.Where(x => !x.StartsWith("SAVE CODE:", StringComparison.OrdinalIgnoreCase)).ToList();

                if (!string.IsNullOrWhiteSpace(session.Username) && (session.AuthMode is "login" or "register"))
                {
                    if (codeLine is not null)
                    {
                        var code = codeLine.Split('|', 2)[0].Replace("SAVE CODE:", "", StringComparison.OrdinalIgnoreCase).Trim();
                        accountRepo.UpdateLastSaveCode(session.Username, code);
                    }

                    lines = [
                        .. lines,
                        "Progress saved to your account."
                    ];
                }
                else
                {
                    lines = [
                        .. lines,
                        "Progress saved for this session."
                    ];
                }
            }

            session.LastTimedPrompt = output.TimedPrompt;
            session.SyncPartyCodeFromEngine();
            return Results.Ok(new
            {
                lines,
                timedPrompt = output.TimedPrompt,
                quitToMenu = lines.Any(x => x.Contains("Leaving game.", StringComparison.OrdinalIgnoreCase)),
                sceneId = session.Engine.GetPlayerOverview().SceneId,
                totalQuestCount = story.Quests.Count
            });
        });

        app.MapPost("/api/sessions/{id}/timed", (string id, TimedRequest request) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            session.Touch();
            var output = session.Engine.ResolveTimed(request.Choice);
            session.LastTimedPrompt = output.TimedPrompt;
            session.SyncPartyCodeFromEngine();
            return Results.Ok(new
            {
                output.Lines,
                timedPrompt = output.TimedPrompt,
                sceneId = session.Engine.GetPlayerOverview().SceneId,
                totalQuestCount = story.Quests.Count
            });
        });

        app.MapGet("/api/sessions/{id}/overview", (string id) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            session.SyncPartyCodeFromEngine();
            var overview = session.Engine.GetPlayerOverview();
            return Results.Ok(new
            {
                playerName = overview.PlayerName,
                lifePath = overview.LifePath,
                classKey = overview.ClassKey,
                inventory = overview.Inventory,
                storedItems = overview.StoredItems,
                partyStoredItems = overview.PartyStoredItems,
                coin = overview.Coin,
                sceneId = overview.SceneId,
                totalQuestCount = story.Quests.Count
            });
        });


        app.MapPost("/api/sessions/{id}/inventory-action", (string id, InventoryActionRequest request) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            session.Touch();
            var item = (request.Item ?? "").Trim();
            var action = (request.Action ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(item))
                return Results.BadRequest(new { error = "item is required" });

            bool ok;
            string message;
            switch (action)
            {
                case "delete":
                    ok = session.Engine.TryDeleteInventoryItem(item, out message);
                    break;
                case "stash":
                    ok = session.Engine.TryStashInventoryItem(item, out message);
                    break;
                default:
                    ok = false;
                    message = "Unknown inventory action.";
                    break;
            }

            if (!ok)
                return Results.BadRequest(new { error = message });

            var overview = session.Engine.GetPlayerOverview();
            return Results.Ok(new
            {
                message,
                inventory = overview.Inventory,
                storedItems = overview.StoredItems,
                partyStoredItems = overview.PartyStoredItems,
                lifePath = overview.LifePath,
                classKey = overview.ClassKey,
                coin = overview.Coin,
                playerName = overview.PlayerName,
                sceneId = overview.SceneId
            });
        });

        app.MapGet("/api/sessions/{id}/inventory-text", (string id) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            var line = session.Engine.GetInventorySnapshotLine();
            return Results.Ok(new { line });
        });

        app.MapGet("/api/sessions/{id}/party", (string id) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            session.SyncPartyCodeFromEngine();
            var overview = session.Engine.GetPartyOverview();
            if (overview is null)
                return Results.Ok(new { partyCode = session.LastKnownPartyCode, members = Array.Empty<object>() });

            return Results.Ok(new
            {
                partyCode = overview.PartyCode,
                members = overview.Members.Select(m => new
                {
                    name = m.Name,
                    lastSceneId = m.LastSceneId,
                    lastSeenUtc = m.LastSeenUtc,
                    secondsSinceSeen = m.SecondsSinceSeen
                }).ToArray()
            });
        });

        app.MapGet("/api/sessions/{id}/chat", (string id, string? channel, long? since, int? limit, string? partyCode) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            session.Touch();
            var normalizedChannel = NormalizeChatChannel(channel);
            if (!TryResolveChatScopeKey(session, normalizedChannel, partyCode, out var scopeKey, out var canPost, out var reason))
                return Results.BadRequest(new { error = reason ?? "chat unavailable" });

            var state = GetOrCreateChatChannel(chatChannels, chatStore, scopeKey);
            var requestedLimit = Math.Clamp(limit ?? 5000, 1, 5000);
            var messages = since.HasValue
                ? state.GetMessagesSince(DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, since.Value)))
                : state.GetRecent(requestedLimit);

            var payload = messages
                .Select(x => new
                {
                    ts = x.TimestampUtc.ToUnixTimeMilliseconds(),
                    author = x.Author,
                    text = x.Text
                })
                .ToArray();

            return Results.Ok(new
            {
                channel = normalizedChannel,
                canPost,
                reason,
                serverNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                messages = payload
            });
        });

        app.MapPost("/api/sessions/{id}/chat", (string id, ChatPostRequest request) =>
        {
            if (!sessions.TryGetValue(id, out var session))
                return Results.NotFound(new { error = "session not found" });

            session.Touch();
            var normalizedChannel = NormalizeChatChannel(request.Channel);
            if (!TryResolveChatScopeKey(session, normalizedChannel, request.PartyCode, out var scopeKey, out var canPost, out var reason))
                return Results.BadRequest(new { error = reason ?? "chat unavailable" });

            if (!canPost)
                return Results.BadRequest(new { error = reason ?? "You cannot post in this chat right now." });

            var text = (request.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "Message cannot be empty." });
            if (text.Length > 180)
                text = text[..180];

            var author = session.Engine.GetPlayerOverview().PlayerName;
            var state = GetOrCreateChatChannel(chatChannels, chatStore, scopeKey);
            var message = new ChatMessage(DateTimeOffset.UtcNow, author, text);
            state.Add(message);
            chatBroadcaster.Publish(scopeKey, message);
            session.SyncPartyCodeFromEngine();

            return Results.Ok(new
            {
                channel = normalizedChannel,
                ts = message.TimestampUtc.ToUnixTimeMilliseconds(),
                message = new { author = message.Author, text = message.Text }
            });
        });

        app.MapGet("/api/sessions/{id}/chat/stream", async (HttpContext context, string id, string? channel, string? partyCode) =>
        {
            if (!sessions.TryGetValue(id, out var session))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = "session not found" });
                return;
            }

            session.Touch();
            var normalizedChannel = NormalizeChatChannel(channel);
            if (!TryResolveChatScopeKey(session, normalizedChannel, partyCode, out var scopeKey, out _, out var reason))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = reason ?? "chat unavailable" });
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Append("X-Accel-Buffering", "no");
            context.Response.ContentType = "text/event-stream";

            var state = GetOrCreateChatChannel(chatChannels, chatStore, scopeKey);
            foreach (var message in state.GetRecent(100))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    ts = message.TimestampUtc.ToUnixTimeMilliseconds(),
                    author = message.Author,
                    text = message.Text
                });
                await context.Response.WriteAsync($"event: message\ndata: {payload}\n\n", context.RequestAborted);
            }
            await context.Response.Body.FlushAsync(context.RequestAborted);

            var subscriptionId = Guid.NewGuid();
            var reader = chatBroadcaster.Subscribe(scopeKey, subscriptionId, context.RequestAborted);
            try
            {
                await foreach (var message in reader.ReadAllAsync(context.RequestAborted))
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        ts = message.TimestampUtc.ToUnixTimeMilliseconds(),
                        author = message.Author,
                        text = message.Text
                    });
                    await context.Response.WriteAsync($"event: message\ndata: {payload}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                chatBroadcaster.Unsubscribe(scopeKey, subscriptionId);
            }
        });

        app.MapDelete("/api/sessions/{id}", (string id) =>
        {
            sessions.TryRemove(id, out _);
            return Results.NoContent();
        });

        return app.RunAsync();
    }


    private static ChatChannelState GetOrCreateChatChannel(ConcurrentDictionary<string, ChatChannelState> channels, ChatTranscriptStore store, string scopeKey)
    {
        return channels.GetOrAdd(scopeKey, key =>
        {
            var seeded = store.Load(key);
            return new ChatChannelState(seeded, snapshot => store.Save(key, snapshot));
        });
    }

    private static string NormalizeChatChannel(string? channel)
    {
        return string.Equals(channel?.Trim(), "party", StringComparison.OrdinalIgnoreCase)
            ? "party"
            : "global";
    }

    private static bool TryResolveChatScopeKey(SessionState session, string channel, string? requestedPartyCode, out string scopeKey, out bool canPost, out string? reason)
    {
        reason = null;
        if (channel == "global")
        {
            scopeKey = "global";
            canPost = true;
            return true;
        }

        session.SyncPartyCodeFromEngine();
        var partyCode = session.Engine.ActivePartyCode;
        if (string.IsNullOrWhiteSpace(partyCode))
            partyCode = session.Engine.GetPartyOverview()?.PartyCode;
        if (string.IsNullOrWhiteSpace(partyCode))
            partyCode = session.LastKnownPartyCode;
        if (string.IsNullOrWhiteSpace(partyCode) && !string.IsNullOrWhiteSpace(requestedPartyCode))
            partyCode = requestedPartyCode.Trim();

        if (string.IsNullOrWhiteSpace(partyCode))
        {
            scopeKey = $"party:solo:{session.Engine.GetPlayerOverview().PlayerName}";
            canPost = false;
            reason = "Join or create a party to use party chat.";
            return true;
        }

        scopeKey = $"party:{partyCode.Trim().ToUpperInvariant()}";
        canPost = true;
        return true;
    }

    private static PlayerSex ParseSex(string? sex)
    {
        return sex?.Trim().ToLowerInvariant() switch
        {
            "female" or "f" => PlayerSex.Female,
            _ => PlayerSex.Male
        };
    }

    private static string? NormalizePlayerName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var cleaned = raw.Trim();
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
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
        public string? LastKnownPartyCode { get; private set; }
        public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch()
            => LastActivityUtc = DateTimeOffset.UtcNow;

        public void SyncPartyCodeFromEngine()
        {
            LastKnownPartyCode = Engine.ActivePartyCode
                ?? Engine.GetPartyOverview()?.PartyCode
                ?? LastKnownPartyCode;
        }
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

public sealed class InventoryActionRequest
{
    public string? Action { get; set; }
    public string? Item { get; set; }
}

public sealed class TimedRequest
{
    public int Choice { get; set; }
}

public sealed class ChatPostRequest
{
    public string? Channel { get; set; }
    public string? Text { get; set; }
    public string? PartyCode { get; set; }
}

internal sealed record ChatMessage(DateTimeOffset TimestampUtc, string Author, string Text);

internal sealed class ChatChannelState
{
    private readonly object _gate = new();
    private readonly List<ChatMessage> _messages = new();
    private readonly Action<IReadOnlyList<ChatMessage>>? _persistSnapshot;
    private static readonly TimeSpan MessageTtl = TimeSpan.FromHours(24);

    public ChatChannelState(IEnumerable<ChatMessage>? seed = null, Action<IReadOnlyList<ChatMessage>>? persistSnapshot = null)
    {
        _persistSnapshot = persistSnapshot;
        if (seed is not null)
            _messages.AddRange(seed.OrderBy(x => x.TimestampUtc));
        PruneExpiredLocked(DateTimeOffset.UtcNow);
    }

    public void Add(ChatMessage message)
    {
        lock (_gate)
        {
            PruneExpiredLocked(DateTimeOffset.UtcNow);
            _messages.Add(message);
            PersistLocked();
        }
    }

    public List<ChatMessage> GetMessagesSince(DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            PruneExpiredLocked(DateTimeOffset.UtcNow);
            return _messages
                .Where(x => x.TimestampUtc > sinceUtc)
                .OrderBy(x => x.TimestampUtc)
                .ToList();
        }
    }

    public List<ChatMessage> GetRecent(int limit)
    {
        lock (_gate)
        {
            PruneExpiredLocked(DateTimeOffset.UtcNow);
            if (_messages.Count == 0) return [];

            var take = Math.Clamp(limit, 1, 5000);
            return _messages
                .OrderByDescending(x => x.TimestampUtc)
                .Take(take)
                .OrderBy(x => x.TimestampUtc)
                .ToList();
        }
    }

    private void PruneExpiredLocked(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc - MessageTtl;
        var removed = _messages.RemoveAll(x => x.TimestampUtc < cutoff);
        if (removed > 0)
            PersistLocked();
    }

    private void PersistLocked()
        => _persistSnapshot?.Invoke(_messages.ToList());
}

internal sealed class ChatTranscriptStore
{
    private readonly string _root;
    private readonly object _ioGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ChatTranscriptStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public List<ChatMessage> Load(string scopeKey)
    {
        var path = PathFor(scopeKey);
        if (!File.Exists(path)) return [];

        lock (_ioGate)
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOptions) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void Save(string scopeKey, IReadOnlyList<ChatMessage> messages)
    {
        var path = PathFor(scopeKey);
        var tmp = path + ".tmp";

        lock (_ioGate)
        {
            Directory.CreateDirectory(_root);
            var json = JsonSerializer.Serialize(messages, JsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
    }

    private string PathFor(string scopeKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scopeKey))).ToLowerInvariant();
        return Path.Combine(_root, hash + ".json");
    }
}


internal sealed class ChatBroadcaster
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ChatMessage>>> _subscribers = new(StringComparer.OrdinalIgnoreCase);

    public ChannelReader<ChatMessage> Subscribe(string scopeKey, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<ChatMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var group = _subscribers.GetOrAdd(scopeKey, _ => new ConcurrentDictionary<Guid, Channel<ChatMessage>>());
        group[subscriptionId] = channel;

        cancellationToken.Register(() => Unsubscribe(scopeKey, subscriptionId));
        return channel.Reader;
    }

    public void Publish(string scopeKey, ChatMessage message)
    {
        if (!_subscribers.TryGetValue(scopeKey, out var group)) return;

        foreach (var kv in group)
            kv.Value.Writer.TryWrite(message);
    }

    public void Unsubscribe(string scopeKey, Guid subscriptionId)
    {
        if (!_subscribers.TryGetValue(scopeKey, out var group)) return;

        if (group.TryRemove(subscriptionId, out var channel))
            channel.Writer.TryComplete();

        if (group.IsEmpty)
            _subscribers.TryRemove(scopeKey, out _);
    }
}
