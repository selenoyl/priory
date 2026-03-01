using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Priory.Game;

public sealed class GameEngine
{
    private static readonly TimeSegment[] SegmentOrder =
    [
        TimeSegment.Matins,
        TimeSegment.Prime,
        TimeSegment.Sext,
        TimeSegment.Vespers,
        TimeSegment.Compline
    ];

    private static readonly Dictionary<string, string[]> WordAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["franciscan"] = ["friar", "monk", "brother"],
        ["friar"] = ["franciscan", "monk", "brother"],
        ["father"] = ["priest", "friar", "cleric"],
        ["priest"] = ["father", "friar", "cleric"],
        ["cart"] = ["wagon", "wagoner"],
        ["wagon"] = ["cart"],
        ["gate"] = ["church gate", "archway"],
        ["church"] = ["chapel"],
        ["market"] = ["square", "market square"],
        ["steward"] = ["reeve", "bursar"]
    };

    private readonly StoryData _story;
    private readonly string _saveRoot;
    private readonly SaveCodec _codec;
    private readonly PartyRepository _partyRepo;
    private readonly Random _rng = new();

    private GameState _state = new();
    private PartyState? _partyState;
    private TimedDef? _activeTimed;
    private List<int>? _activeTimedOptionIndexes;

    public bool IsEnded { get; private set; }

    public GameEngine(StoryData story, string saveRoot, SaveCodec codec)
    {
        _story = story;
        _saveRoot = saveRoot;
        _codec = codec;
        _partyRepo = new PartyRepository(saveRoot, codec);
    }

    public string? ActivePartyCode { get; private set; }

    public bool IsPartyMode => _partyState is not null;

    public void StartNewGame(string playerName, PlayerSex sex = PlayerSex.Male)
    {
        _state = new GameState
        {
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Pilgrim" : playerName.Trim(),
            Sex = sex
        };
        EnsureRebuildState();
        AttachSharedState();
        IsEnded = false;
        _state.SceneId = "intro";
        _state.ActiveMenuId = "life_path";
        RegisterPartyMember();
        PersistParty();
        foreach (var line in OpeningLoreBrief())
            Console.WriteLine(line);
        Console.WriteLine($"Welcome, {_state.PlayerName}.");
        Console.WriteLine($"You begin as a {SexLabel(_state.Sex)} pilgrim.");
        if (IsPartyMode)
            Console.WriteLine($"Party bound to Saint Catherine with code: {ActivePartyCode}");
        foreach (var tip in GameplayPrimer())
            Console.WriteLine(tip);
        Console.WriteLine(CurrentScene().Text);
        Console.WriteLine(RenderMenu(_story.Menus["life_path"]));
    }

    public string CreateParty()
    {
        var created = _partyRepo.CreateParty();
        _partyState = created.Party;
        ActivePartyCode = created.PartyCode;
        return created.PartyCode;
    }

    public bool JoinParty(string partyCode, out string message)
        => JoinParty(partyCode, _state.PlayerName, out message);

    public bool JoinParty(string partyCode, string? prospectivePlayerName, out string message)
    {
        if (!_partyRepo.TryLoadByCode(partyCode, out var party, out message) || party is null)
            return false;

        var playerName = string.IsNullOrWhiteSpace(prospectivePlayerName) ? _state.PlayerName : prospectivePlayerName.Trim();
        if (party.Members.Count >= 6 && !party.Members.ContainsKey(playerName))
        {
            message = "That party is full (limit 6 members).";
            return false;
        }

        _partyState = party;
        ActivePartyCode = _codec.MakePartyCode(party.PartyId);
        return true;
    }

    public PartyOverview? GetPartyOverview()
    {
        if (_partyState is null) return null;

        var members = _partyState.Members.Values
            .OrderBy(x => x.Name)
            .Select(m => new PartyMemberOverview(m.Name, m.LastSceneId, m.LastSeenUtc,
                Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - m.LastSeenUtc).TotalSeconds))))
            .ToList();

        return new PartyOverview(_codec.MakePartyCode(_partyState.PartyId), members);
    }

    public PlayerOverview GetPlayerOverview()
    {
        var inventory = _state.Inventory
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlayerOverview(_state.PlayerName, _state.Sex, _state.LifePath, inventory, _state.Coin);
    }

    public void UseSoloMode()
    {
        _partyState = null;
        ActivePartyCode = null;
    }

    public bool TryResume(string code, out string message)
    {
        try
        {
            var saveId = _codec.VerifyCode(code);
            var path = Path.Combine(_saveRoot, saveId + ".json");
            if (!File.Exists(path))
            {
                message = "Resume code valid, but save file not found.";
                return false;
            }

            _state = JsonSerializer.Deserialize<GameState>(File.ReadAllText(path)) ?? new GameState();
            if (_state.Sex == PlayerSex.Unknown)
                _state.Sex = PlayerSex.Male;
            EnsureRebuildState();
            if (!string.IsNullOrWhiteSpace(_state.PartyId) && _partyRepo.TryLoadById(_state.PartyId, out var shared) && shared is not null)
            {
                _partyState = shared;
                ActivePartyCode = _codec.MakePartyCode(shared.PartyId);
                AttachSharedState();
            }
            else
            {
                _partyState = null;
                ActivePartyCode = null;
            }

            RegisterPartyMember();
            IsEnded = false;
            message = $"Resumed saved journey for {_state.PlayerName}.";
            Console.WriteLine("Quick refresher: type a verb first (like 'look', 'go gate', or 'talk steward').");
            Console.WriteLine("Type 'help' anytime to see common world interactions.");
            Console.WriteLine(CurrentScene().Text);
            Console.WriteLine(ExitLine(CurrentScene()));
            Console.WriteLine(TimeLine());
            if (_state.ActiveMenuId is { } menuId && _story.Menus.TryGetValue(menuId, out var menu))
                Console.WriteLine(RenderMenu(menu));
            return true;
        }
        catch
        {
            message = "Could not verify resume code.";
            return false;
        }
    }

    public EngineOutput HandleInput(string raw)
    {
        SyncPartyState();
        var lines = new List<string>();
        var parsed = Parser.Parse(raw);

        if (_state.ActiveMenuId is { } menuId)
            return ResolveMenu(parsed, menuId);

        if (_activeTimed is not null)
            return new(new List<string> { "Timed prompt active. Enter a number." });

        switch (parsed.Intent)
        {
            case Intent.Help:
                lines.Add("How input works: start with a verb, then optionally a target. Examples: 'look', 'go priory gate', 'talk steward', 'examine ledger', 'take wax candles'.");
                lines.Add("Common actions: look, go/enter <place>, talk/speak <person>, examine <thing>, take <item>, inventory, status, virtues, rebuild, quests, party, save, version, quit.");
                lines.Add("Tip: number choices (1, 2, 3...) select menu/timed options when they are shown.");
                break;
            case Intent.Look:
                lines.Add(CurrentScene().Text);
                lines.Add(ExitLine(CurrentScene()));
                AppendPartyLoreRumors(lines);
                lines.Add(TimeLine());
                AddContextTip(lines);
                break;
            case Intent.Inventory:
                lines.Add(InventoryLine());
                break;
            case Intent.Status:
                lines.Add(TimeLine());
                lines.Add(PrioryStatusLine());
                lines.Add(WorkTotalsLine());
                break;
            case Intent.Save:
                lines.Add(SaveNow());
                break;
            case Intent.Party:
                lines.Add(PartyStatusLine());
                break;
            case Intent.Version:
                lines.Add(VersionLine());
                break;
            case Intent.Virtues:
                lines.Add(VirtueDiagram());
                break;
            case Intent.Rebuild:
                HandleRebuildCommand(parsed, lines);
                break;
            case Intent.Quests:
                lines.Add(QuestLog());
                break;
            case Intent.Quit:
                IsEnded = true;
                lines.Add("Leaving game.");
                break;
            case Intent.Go:
                HandleMovement(parsed, lines);
                break;
            case Intent.Talk:
            case Intent.Examine:
            case Intent.Take:
                if (string.Equals(parsed.Target, "quests", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(QuestLog());
                    break;
                }
                AppendPartyLoreRumors(lines);
                HandleAction(parsed, lines);
                break;
            default:
                lines.Add("That verb is not recognized here. Try 'help' for allowed commands.");
                break;
        }

        MaybeAddPeriodicTip(lines, parsed.Intent);
        PersistParty();
        var timedPrompt = MaybeActivateTimed(lines);
        return new(lines, timedPrompt);
    }

    private static string VersionLine()
    {
        var buildId = Environment.GetEnvironmentVariable("PRIORY_BUILD_ID") ?? "dev";
        return $"Version: {buildId}";
    }


    private static bool IsLikelyConversationalTarget(string key)
    {
        var k = key.Trim().ToLowerInvariant();
        var peopleTokens = new[]
        {
            "father","mother","friar","friars","brother","abbess","prioress","steward","clerk","guardian","lector",
            "bishop","masters","master","factor","martin","visitor","prior"
        };
        return peopleTokens.Any(t => k.Contains(t));
    }

    private static string FormatExitHint(string key)
    {
        var k = key.Trim();
        if (k.StartsWith("leave ", StringComparison.OrdinalIgnoreCase) || k.StartsWith("exit ", StringComparison.OrdinalIgnoreCase))
            return k.ToLowerInvariant();
        return $"go {k}";
    }

    private void AddContextTip(List<string> lines)
    {
        if (_state.ActiveMenuId is not null) return;

        var scene = CurrentScene();
        var exits = AvailableExits(scene).Keys.OrderBy(x => x).ToList();
        var actionTips = AvailableActions(scene)
            .OrderBy(kv => kv.Key)
            .Take(5)
            .Select(kv => SuggestActionPhrase(kv.Key, kv.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scene.Exits.ContainsKey("outside")
            && !actionTips.Any(x => x.Equals("go outside", StringComparison.OrdinalIgnoreCase)))
        {
            actionTips.Add("go outside");
        }

        if (exits.Count > 0)
            lines.Add($"Available options: {string.Join(" | ", exits.Select(FormatExitHint))}.");

        if (actionTips.Count > 0)
            lines.Add($"You can also try: {string.Join(" | ", actionTips)}.");

        if (exits.Count == 0 && actionTips.Count == 0)
            lines.Add("Available options: look | quests | status | help.");
    }

    private static string SuggestActionPhrase(string key, string result)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        if (result.StartsWith("menu:"))
            return IsLikelyConversationalTarget(key) ? $"talk {key}" : $"examine {key}";
        if (result.StartsWith("scene:"))
            return $"go {key}";
        if (result.StartsWith("timed:"))
            return $"examine {key}";
        if (result.StartsWith("script:"))
            return string.Empty;

        return $"examine {key}";
    }

    private void MaybeAddPeriodicTip(List<string> lines, Intent intent)
    {
        if (intent is Intent.Help or Intent.Save or Intent.Quit or Intent.Version or Intent.Unknown)
            return;

        var turns = _state.Counters.GetValueOrDefault("turn_count") + 1;
        _state.Counters["turn_count"] = turns;

        if (turns == 2)
        {
            lines.Add("Tip: 'look' reprints your current scene and available exits if you feel lost.");
            return;
        }

        if (turns == 4)
        {
            lines.Add("Tip: use 'quests' to review active objectives and 'status' to check priory health.");
            return;
        }

        if (turns == 7)
        {
            lines.Add("Tip: use 'inventory' often, and type 'help' anytime for examples of command format.");
        }
    }

    private static IEnumerable<string> OpeningLoreBrief()
    {
        yield return "England, Anno Domini 1403.";
        yield return "You arrive in Blackpine, Yorkshire, where abbey bells, guild quarrels, and lordly levies shape every day.";
        yield return "Rebellion scars still mark the kingdom, roads are thick with rumor, and hungry winters test both faith and law.";
        yield return "Saint Catherine Priory endures, but its walls are weary: stores run thin, roofs strain, and rival factions watch your choices.";
        yield return "Here, mercy must be practical, piety must survive politics, and every promise has a cost.";
        yield return "";
    }

    private static IEnumerable<string> GameplayPrimer()
    {
        yield return "How to play: this world uses a verb processor. Enter a verb first, then what you want to act on.";
        yield return "Examples: 'look', 'go gate', 'enter cart', 'talk steward', 'examine ledger', 'take candles'.";
        yield return "Use number choices when a menu appears. Type 'virtues' for your trait chart and 'help' for a quick refresher.";
    }

    public EngineOutput ResolveTimed(int selected)
    {
        SyncPartyState();
        var lines = new List<string>();
        if (_activeTimed is null)
            return new(new List<string> { "No timed event is active." });

        var displayIndex = selected - 1;
        int index;
        if (_activeTimedOptionIndexes is null || displayIndex < 0 || displayIndex >= _activeTimedOptionIndexes.Count)
        {
            index = ChooseDefaultTimedIndex(_activeTimed);
            lines.Add("You hesitate. The moment chooses for you.");
        }
        else
        {
            index = _activeTimedOptionIndexes[displayIndex];
        }

        var option = _activeTimed.Options[index];
        if (!IsOptionAvailable(option, out var why, _activeTimed is null ? null : $"timed:{_activeTimed.Id}:{index}"))
        {
            lines.Add($"That path is unavailable: {why}");
            index = ChooseDefaultTimedIndex(_activeTimed);
            option = _activeTimed.Options[index];
        }

        ApplyOption(option, lines, _activeTimed is null ? null : $"timed:{_activeTimed.Id}:{index}");
        _activeTimed = null;
        _activeTimedOptionIndexes = null;
        _state.ActiveTimedId = null;
        _state.ActiveTimedDeadline = null;

        AdvanceTime(lines, 1);
        PersistParty();
        return new(lines, MaybeActivateTimed(lines));
    }

    private EngineOutput ResolveMenu(ParsedInput parsed, string menuId)
    {
        var lines = new List<string>();
        if (!_story.Menus.TryGetValue(menuId, out var menu))
        {
            _state.ActiveMenuId = null;
            PersistParty();
            return new(new List<string> { "Menu missing; continuing." });
        }

        if (parsed.Intent == Intent.Save)
        {
            var output = new EngineOutput(new List<string> { SaveNow(), RenderMenu(menu) });
            PersistParty();
            return output;
        }

        if (parsed.Intent != Intent.Numeric)
            return new(new List<string> { "Choose a number.", "Tip: when a menu is open, enter 1, 2, 3... to pick an option.", RenderMenu(menu) });

        var available = menu.Options
            .Select((o, i) => (o, i))
            .Where(x => IsOptionAvailable(x.o, out _, $"menu:{menu.Id}:{x.i}"))
            .ToList();

        var index = parsed.Number - 1;
        if (index < 0 || index >= available.Count)
            return new(new List<string> { "That option is not available.", RenderMenu(menu) });

        if (menu.Id == "life_path")
        {
            ApplyLifePath(available[index].i, lines);
            PersistParty();
            return new(lines, MaybeActivateTimed(lines));
        }

        var option = available[index].o;
        _state.ActiveMenuId = null;
        ApplyOption(option, lines, $"menu:{menu.Id}:{available[index].i}");

        if (!(option.Script?.StartsWith("task:") ?? false))
            AdvanceTime(lines, 1);

        if (menu.Id.StartsWith("intro_", StringComparison.OrdinalIgnoreCase) && _state.ActiveMenuId is null)
            AddContextTip(lines);

        return new(lines, MaybeActivateTimed(lines));
    }

    private void ApplyLifePath(int index, List<string> lines)
    {
        var key = _story.LifePaths.Keys.OrderBy(x => x).ElementAt(index);
        var lp = _story.LifePaths[key];

        _state.ActiveMenuId = null;
        _state.LifePath = lp.Name;

        foreach (var kv in lp.VirtueDelta)
            AddVirtue(kv.Key, kv.Value);

        var lifePathVirtueSummary = FormatVirtueChanges(lp.VirtueDelta);

        _state.Coin = _rng.Next(lp.CoinMin, lp.CoinMax + 1);
        foreach (var item in lp.StarterItems)
            if (!_state.Inventory.Contains(item)) _state.Inventory.Add(item);

        lines.Add($"You have chosen: {lp.Name}");
        lines.Add($"Starting coin: {_state.Coin} silver pennies.");
        lines.Add("Starting items: " + string.Join(", ", lp.StarterItems));
        if (!string.IsNullOrWhiteSpace(lifePathVirtueSummary))
            lines.Add($"Virtues adjusted: {lifePathVirtueSummary}");
        lines.Add(VirtueDiagram());

        _state.SceneId = "house";
        lines.Add(CurrentScene().Text);
        AppendPartyLoreRumors(lines);

        if (!string.IsNullOrWhiteSpace(lp.IntroMenu))
        {
            _state.ActiveMenuId = lp.IntroMenu;
            AppendMenuWithContext(lp.IntroMenu, lines);
        }
        else
        {
            lines.Add(ExitLine(CurrentScene()));
            AddContextTip(lines);
        }

        StartQuest("main_rebuild_priory", lines);
    }


    private static string BuildMenuChoiceFlag(string menuId, string optionText)
        => $"menu_choice_taken:{NormalizePhrase(menuId).Replace(' ', '_')}:{NormalizePhrase(optionText).Replace(' ', '_')}";

    private bool IsRepeatableMenu(string menuId)
    {
        if (string.IsNullOrWhiteSpace(menuId)) return false;
        var id = menuId.ToLowerInvariant();
        return id.Contains("shop") || id.Contains("task_board") || id.Contains("pouch") || id.Contains("book_list") || id.Contains("watch_log");
    }

    private bool IsConsequentialDecision(string menuId, MenuOptionDef option)
    {
        if (IsRepeatableMenu(menuId)) return false;
        if (option.Script?.StartsWith("task:") == true) return false;
        if (option.Script?.StartsWith("shop:") == true) return false;
        if (option.Script?.StartsWith("minigame:") == true) return false;

        return option.PrioryDelta is { Count: > 0 }
               || option.VirtueDelta is { Count: > 0 }
               || option.SetFlags is { Count: > 0 }
               || option.ClearFlags is { Count: > 0 }
               || option.CounterDelta is { Count: > 0 }
               || !string.IsNullOrWhiteSpace(option.StartQuest)
               || !string.IsNullOrWhiteSpace(option.CompleteQuest)
               || !string.IsNullOrWhiteSpace(option.NextScene)
               || !string.IsNullOrWhiteSpace(option.NextMenu)
               || !string.IsNullOrWhiteSpace(option.NextTimed);
    }

    private string? BuildMenuChoiceReminder(string menuId)
    {
        var prefix = $"menu_choice_taken:{NormalizePhrase(menuId).Replace(' ', '_')}:";
        var found = _state.Flags
            .FirstOrDefault(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(found)) return null;

        var choiceSlug = found[prefix.Length..].Replace('_', ' ');
        if (string.IsNullOrWhiteSpace(choiceSlug)) return null;
        return $"You already made this decision earlier ({choiceSlug}). That commitment still stands.";
    }

    private bool IsOptionAvailable(MenuOptionDef option, out string reason, string? sourceContext = null)
    {
        reason = "";

        if (!string.IsNullOrWhiteSpace(sourceContext) && sourceContext.StartsWith("menu:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = sourceContext.Split(':', 3);
            if (parts.Length >= 2)
            {
                var menuId = parts[1];
                if (IsConsequentialDecision(menuId, option) && _state.Flags.Contains(BuildMenuChoiceFlag(menuId, option.Text)))
                {
                    reason = "already chosen";
                    return false;
                }
            }
        }

        if (option.RequireFlags is { Count: > 0 })
        {
            foreach (var flag in option.RequireFlags)
            {
                if (!_state.Flags.Contains(flag))
                {
                    reason = $"missing flag '{flag}'";
                    return false;
                }
            }
        }

        if (option.RequireNotFlags is { Count: > 0 })
        {
            foreach (var flag in option.RequireNotFlags)
            {
                if (_state.Flags.Contains(flag))
                {
                    reason = $"blocked by flag '{flag}'";
                    return false;
                }
            }
        }

        if (option.RequireSexes is { Count: > 0 } && !option.RequireSexes.Any(IsCurrentSex))
        {
            reason = "not available for your sex";
            return false;
        }

        if (option.RequireNotSexes is { Count: > 0 } && option.RequireNotSexes.Any(IsCurrentSex))
        {
            reason = "not available for your sex";
            return false;
        }

        if (ViolatesClericalRoleRestrictions(option, out var sexReason))
        {
            reason = sexReason;
            return false;
        }

        if (option.CoinDelta < 0 && _state.Coin < Math.Abs(option.CoinDelta))
        {
            reason = "insufficient coin";
            return false;
        }

        if (option.RemoveItems is { Count: > 0 })
        {
            foreach (var item in option.RemoveItems)
            {
                if (!_state.Inventory.Contains(item))
                {
                    reason = $"missing item '{item}'";
                    return false;
                }
            }
        }

        return true;
    }

    private void ApplyOption(MenuOptionDef option, List<string> lines, string? sourceContext = null)
    {
        if (!string.IsNullOrWhiteSpace(option.Response)) lines.Add(option.Response);

        if (option.VirtueDelta is not null)
        {
            var virtueGrantKey = BuildVirtueGrantKey(sourceContext, option);
            if (virtueGrantKey is not null && _state.Flags.Contains(virtueGrantKey))
            {
                lines.Add("You have already taken the spiritual fruit of this choice; no further virtue is gained.");
            }
            else
            {
                foreach (var delta in option.VirtueDelta)
                    AddVirtue(delta.Key, delta.Value);

                var virtueSummary = FormatVirtueChanges(option.VirtueDelta);
                if (!string.IsNullOrWhiteSpace(virtueSummary))
                    lines.Add($"Virtues adjusted: {virtueSummary}");
                lines.Add(VirtueDiagram());

                if (virtueGrantKey is not null)
                    _state.Flags.Add(virtueGrantKey);
            }
        }

        if (option.PrioryDelta is not null)
            foreach (var delta in option.PrioryDelta)
                _state.Priory[delta.Key] = Math.Clamp(_state.Priory.GetValueOrDefault(delta.Key) + delta.Value, 0, 100);

        if (option.CounterDelta is not null)
            foreach (var delta in option.CounterDelta)
                _state.Counters[delta.Key] = _state.Counters.GetValueOrDefault(delta.Key) + delta.Value;

        if (option.SetFlags is not null)
            foreach (var flag in option.SetFlags)
            {
                if (_state.Flags.Add(flag))
                    RecordLoreEvent($"{_state.PlayerName} set events in motion: {LoreFriendlyFlag(flag)}");
            }

        if (option.ClearFlags is not null)
            foreach (var flag in option.ClearFlags)
                _state.Flags.Remove(flag);

        _state.Coin += option.CoinDelta;

        if (option.AddItems is not null)
            foreach (var item in option.AddItems.Where(i => !_state.Inventory.Contains(i)))
                _state.Inventory.Add(item);

        if (option.RemoveItems is not null)
            foreach (var item in option.RemoveItems)
                _state.Inventory.Remove(item);

        if (!string.IsNullOrWhiteSpace(option.StartQuest))
            StartQuest(option.StartQuest, lines);

        if (!string.IsNullOrWhiteSpace(option.CompleteQuest))
            CompleteQuest(option.CompleteQuest, lines);

        if (!string.IsNullOrWhiteSpace(sourceContext) && sourceContext.StartsWith("menu:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = sourceContext.Split(':', 3);
            if (parts.Length >= 2)
            {
                var menuId = parts[1];
                if (IsConsequentialDecision(menuId, option))
                    _state.Flags.Add(BuildMenuChoiceFlag(menuId, option.Text));
            }
        }

        if (!string.IsNullOrWhiteSpace(option.Script))
            RunScript(option.Script, lines);

        if (!string.IsNullOrWhiteSpace(option.NextScene))
        {
            MoveToScene(option.NextScene, lines);
            if (CurrentScene().EndChapter) IsEnded = true;
        }

        if (!string.IsNullOrWhiteSpace(option.NextMenu))
        {
            _state.ActiveMenuId = option.NextMenu;
            AppendMenuWithContext(option.NextMenu, lines);
        }

        if (!string.IsNullOrWhiteSpace(option.NextTimed))
            _state.ActiveTimedId = option.NextTimed;
    }

    private void RunScript(string script, List<string> lines)
    {
        if (script.StartsWith("task:"))
        {
            ExecuteTask(script[5..], lines);
            return;
        }

        if (script.StartsWith("shop:"))
        {
            OpenShop(script[5..], lines);
            return;
        }

        if (script.StartsWith("minigame:"))
        {
            PlayMiniGame(script[9..], lines);
            return;
        }

        if (script.StartsWith("chance:"))
        {
            ResolveChanceEvent(script[7..], lines);
            return;
        }

        if (script.StartsWith("rebuild:"))
        {
            ResolveRebuildScript(script[8..], lines);
            return;
        }

        switch (script)
        {
            case "check_progress":
                CheckProgress(lines);
                return;
            case "goto_village_arc":
                GateArc("arc_village", "village_crisis", "Blackpine has not yet brought this dispute formally to Saint Catherine. Continue your ordinary labors.", lines);
                return;
            case "goto_york_arc":
                GateArc("arc_york", "york_letters", "No summons from York has yet arrived.", lines);
                return;
            case "goto_winter_arc":
                GateArc("arc_longwinter", "long_winter", "Winter has not yet forced the priory into emergency measures.", lines);
                return;
            case "goto_avignon_arc":
                GateArc("arc_avignon", "avignon_chapterhouse", "No Avignon-linked patronal packet has yet reached Saint Catherine.", lines);
                return;
            case "goto_bohemia_arc":
                GateArc("arc_bohemia", "bohemian_market", "No credible warning from Prague has yet reached Blackpine.", lines);
                return;
            case "goto_cloth_arc":
                GateArc("arc_cloth", "cloth_ledger_house", "Trade pressure has not yet tightened enough to force new cloth arrangements.", lines);
                return;
            case "goto_shells_arc":
                GateArc("arc_shells", "pilgrim_hostel", "The great pilgrim road has not yet opened through Blackpine.", lines);
                return;
            case "goto_border_arc":
                GateArc("arc_border", "border_refuge", "No border deputation has yet asked Saint Catherine for aid.", lines);
                return;
            case "goto_sealed_room_arc":
                GateArc("arc_sealed_room", "priory_sealed_room", "The priory's internal crisis has not yet broken into daylight.", lines);
                return;
            case "resolve_endgame":
                ResolveEndgame(lines);
                return;
            case "questlog":
                lines.Add(QuestLog());
                return;
            case "exchange_to_mark":
                ExchangePenceToMark(lines);
                return;
            case "exchange_to_pence":
                ExchangeMarkToPence(lines);
                return;
            case "start_hanse_arc":
                SetWorldFlag("arc_hanse_letters");
                StartQuest("hanse_letters_packet", lines);
                lines.Add("A sealed Hanse packet pulls Saint Catherine into coastal contracts and scrutiny.");
                return;
            case "start_germany_arc":
                SetWorldFlag("arc_reich_letters");
                StartQuest("reich_theology_letters", lines);
                lines.Add("Letters from the Rhineland studia raise grave theological and pastoral questions.");
                return;
            case "day_loop":
                ResolveDayLoop(lines);
                return;
            case "monk_formation":
                ResolveMonkFormation(lines);
                return;
            case "bulletin_board":
                ResolveBulletinBoard(lines);
                return;
            case "reputation_check":
                ResolveReputationCheck(lines);
                return;
            case "collections_review":
                ResolveCollectionsReview(lines);
                return;
            case "travel_hazard":
                ResolveTravelHazard(lines);
                return;
            case "tight_crafting":
                ResolveTightCrafting(lines);
                return;
            case "virtue_trial":
                ResolveVirtueTrial(lines);
                return;
            case "rebuild_overview":
                RenderRebuildOverview(lines);
                return;
            case "rebuild_plan":
                RenderRebuildPlan(lines);
                return;
            case "rebuild_assign_balanced":
                AssignLaborPreset("balanced", lines);
                return;
        }
    }

    private void ResolveDayLoop(List<string> lines)
    {
        var totalTasks = _state.Counters.GetValueOrDefault("task_total");
        var segmentsElapsed = _state.Counters.GetValueOrDefault("segments_elapsed_today");
        lines.Add($"Day loop ledger: total labors {totalTasks}, segments elapsed today {segmentsElapsed}/5.");

        if (segmentsElapsed >= 4)
        {
            AddVirtue("temperance", 1);
            _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") + 1, 0, 100);
            lines.Add("You close the day in order: temperance +1, morale +1.");
        }
        else
        {
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") - 1, 0, 100);
            lines.Add("Loose scheduling leaves gaps in supervision. Security -1.");
        }

        _state.Counters["segments_elapsed_today"] = 0;
        AdvanceTime(lines, SegmentOrder.Length);
    }

    private void ResolveMonkFormation(List<string> lines)
    {
        var reputation = ComputeReputationScore();
        var novices = _state.Counters.GetValueOrDefault("formation_novices");

        if (reputation < 18)
        {
            lines.Add("Formation inquiry deferred: village trust and priory witness are not yet steady enough.");
            lines.Add("Raise relations, piety, and humility before admitting more novices.");
            return;
        }

        _state.Counters["formation_novices"] = novices + 1;
        _state.Priory["piety"] = Math.Clamp(_state.Priory.GetValueOrDefault("piety") + 1, 0, 100);
        _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") + 1, 0, 100);
        lines.Add($"A novice is admitted to first formation conference. Novices in formation: {_state.Counters["formation_novices"]}.");
        lines.Add("Piety +1, morale +1.");
        AdvanceTime(lines, 1);
    }

    private void ResolveBulletinBoard(List<string> lines)
    {
        var openHazards = _state.Counters.GetValueOrDefault("hazards_resolved");
        lines.Add("Bulletin board:"
            + $" patrol quotas {_state.Counters.GetValueOrDefault("task_patrol")},"
            + $" field quotas {_state.Counters.GetValueOrDefault("task_fields")},"
            + $" charity queues {_state.Counters.GetValueOrDefault("task_charity")},"
            + $" hazards resolved {openHazards}.");

        if (_state.Priory.GetValueOrDefault("food") <= 25)
            lines.Add("Notice: food stores critical. Prioritize fields or disciplined alms rationing.");
        if (_state.Priory.GetValueOrDefault("security") <= 25)
            lines.Add("Notice: road watch thinning. Assign patrols before next market wave.");
    }

    private void ResolveReputationCheck(List<string> lines)
    {
        var score = ComputeReputationScore();
        lines.Add($"Reputation check: {score} (relations + piety + humility + charity).");
        if (score >= 24)
            lines.Add("Standing: Trusted. High-risk petitions and mediated disputes will usually open.");
        else if (score >= 16)
            lines.Add("Standing: Recognized. Ordinary requests proceed, but contentious appeals may resist.");
        else
            lines.Add("Standing: Fragile. Some doors remain closed until witness and prudence improve.");
    }

    private void ResolveCollectionsReview(List<string> lines)
    {
        var collection = _state.Inventory
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (collection.Count == 0)
        {
            lines.Add("Collections cabinet is empty. Recover evidence, tools, and devotional objects to stock it.");
            return;
        }

        lines.Add("Collections cabinet (top holdings):");
        foreach (var entry in collection)
            lines.Add($"  - {entry.Key} x{entry.Count()}");

        _state.Counters["collection_reviews"] = _state.Counters.GetValueOrDefault("collection_reviews") + 1;
    }

    private void ResolveTravelHazard(List<string> lines)
    {
        var roll = _rng.Next(1, 21) + Virtue("fortitude") + Virtue("temperance");
        if (roll >= 19)
        {
            lines.Add("Travel hazard contained: your escort spacing and route timing avert an ambush.");
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 2, 0, 100);
            _state.Counters["hazards_resolved"] = _state.Counters.GetValueOrDefault("hazards_resolved") + 1;
            lines.Add("Security +2.");
        }
        else
        {
            lines.Add("Travel hazard strikes: wagon axle damage and frightened pilgrims slow the route.");
            _state.Priory["treasury"] = Math.Clamp(_state.Priory.GetValueOrDefault("treasury") - 1, 0, 100);
            _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") - 1, 0, 100);
            lines.Add("Treasury -1, morale -1.");
        }

        AdvanceTime(lines, 1);
    }

    private void ResolveTightCrafting(List<string> lines)
    {
        var hasFiber = _state.Inventory.Contains("Comfrey Bundle") || _state.Inventory.Contains("Charcoal Sack");
        var hasBinding = _state.Inventory.Contains("Blessed Cloth") || _state.Inventory.Contains("Timber Planks");
        if (!hasFiber || !hasBinding)
        {
            lines.Add("Tight crafting failed: you lack paired materials (fiber + binding). Try herbal/wood tasks first.");
            return;
        }

        if (!_state.Inventory.Contains("Field Bandage Kit"))
            _state.Inventory.Add("Field Bandage Kit");
        AddVirtue("humility", 1);
        _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") + 1, 0, 100);
        lines.Add("You craft a compact field bandage kit under tight constraints. Humility +1, morale +1.");
        AdvanceTime(lines, 1);
    }

    private void ResolveVirtueTrial(List<string> lines)
    {
        var test = _rng.Next(0, 3);
        if (test == 0)
        {
            var score = Virtue("charity") + Virtue("temperance") + _rng.Next(1, 7);
            if (score >= 9)
            {
                lines.Add("Virtue trial (mercy vs reserves): you ration aid without abandoning the weakest.");
                _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 1, 0, 100);
                AddVirtue("charity", 1);
            }
            else
            {
                lines.Add("Virtue trial (mercy vs reserves): your plan confuses both storekeepers and petitioners.");
                _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") - 1, 0, 100);
            }
        }
        else if (test == 1)
        {
            var score = Virtue("faith") + Virtue("humility") + _rng.Next(1, 7);
            if (score >= 9)
            {
                lines.Add("Virtue trial (truth under pressure): you answer plainly and keep confidence intact.");
                _state.Priory["piety"] = Math.Clamp(_state.Priory.GetValueOrDefault("piety") + 1, 0, 100);
            }
            else
            {
                lines.Add("Virtue trial (truth under pressure): your witness is sound, but poorly timed.");
                _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") - 1, 0, 100);
            }
        }
        else
        {
            var score = Virtue("fortitude") + Virtue("hope") + _rng.Next(1, 7);
            if (score >= 9)
            {
                lines.Add("Virtue trial (fear at dusk): you steady the line and complete the watch.");
                _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 1, 0, 100);
            }
            else
            {
                lines.Add("Virtue trial (fear at dusk): order holds, but confidence thins.");
                _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") - 1, 0, 100);
            }
        }

        AdvanceTime(lines, 1);
    }

    private int ComputeReputationScore()
        => _state.Priory.GetValueOrDefault("relations") / 10
           + _state.Priory.GetValueOrDefault("piety") / 10
           + Math.Max(0, Virtue("humility"))
           + Math.Max(0, Virtue("charity"));

    private void HandleRebuildCommand(ParsedInput parsed, List<string> lines)
    {
        EnsureRebuildState();
        var target = parsed.Target?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target) || target.Equals("overview", StringComparison.OrdinalIgnoreCase))
        {
            RenderRebuildOverview(lines);
            lines.Add("Tip: 'rebuild plan', 'rebuild upgrade <node>', or 'rebuild assign <balanced|aggressive|cautious>'.");
            return;
        }

        if (target.StartsWith("plan", StringComparison.OrdinalIgnoreCase))
        {
            RenderRebuildPlan(lines);
            return;
        }

        if (target.StartsWith("upgrade", StringComparison.OrdinalIgnoreCase))
        {
            var nodeQuery = target["upgrade".Length..].Trim();
            StartRebuildUpgrade(nodeQuery, lines);
            return;
        }

        if (target.StartsWith("assign", StringComparison.OrdinalIgnoreCase))
        {
            var preset = target["assign".Length..].Trim();
            AssignLaborPreset(string.IsNullOrWhiteSpace(preset) ? "balanced" : preset, lines);
            return;
        }

        lines.Add("Unknown rebuild command. Use: rebuild, rebuild plan, rebuild upgrade <node>, rebuild assign <preset>.");
    }

    private void ResolveRebuildScript(string verb, List<string> lines)
    {
        EnsureRebuildState();
        if (string.IsNullOrWhiteSpace(verb))
        {
            RenderRebuildOverview(lines);
            return;
        }

        if (verb.Equals("overview", StringComparison.OrdinalIgnoreCase))
        {
            RenderRebuildOverview(lines);
            return;
        }

        if (verb.Equals("plan", StringComparison.OrdinalIgnoreCase))
        {
            RenderRebuildPlan(lines);
            return;
        }

        if (verb.StartsWith("upgrade/", StringComparison.OrdinalIgnoreCase))
        {
            StartRebuildUpgrade(verb[8..], lines);
            return;
        }

        if (verb.StartsWith("assign/", StringComparison.OrdinalIgnoreCase))
        {
            AssignLaborPreset(verb[7..], lines);
            return;
        }

        lines.Add("Rebuild script call was invalid.");
    }

    private void EnsureRebuildState()
    {
        _state.Rebuild ??= new RebuildState();
        _state.Rebuild.Stats ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "stability", "defense", "hospitality", "sanctity", "scholarship", "economy" })
            _state.Rebuild.Stats[key] = _state.Rebuild.Stats.GetValueOrDefault(key);
        _state.Rebuild.NodeLevels ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _state.Rebuild.LaborAssigned ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _state.Rebuild.LaborAssigned["monks"] = _state.Rebuild.LaborAssigned.GetValueOrDefault("monks", 2);
        _state.Rebuild.LaborAssigned["laybrothers"] = _state.Rebuild.LaborAssigned.GetValueOrDefault("laybrothers", 1);
        _state.Rebuild.LaborAssigned["workers"] = _state.Rebuild.LaborAssigned.GetValueOrDefault("workers", 0);
        _state.Rebuild.VisitorCapacity = Math.Max(1, _state.Rebuild.VisitorCapacity);
    }

    private void RenderRebuildOverview(List<string> lines)
    {
        EnsureRebuildState();
        var rb = _state.Rebuild;
        lines.Add("Saint Catherine Rebuild Planner");
        lines.Add($"Stats: Stability {rb.Stats.GetValueOrDefault("stability")}, Defense {rb.Stats.GetValueOrDefault("defense")}, Hospitality {rb.Stats.GetValueOrDefault("hospitality")}, Sanctity {rb.Stats.GetValueOrDefault("sanctity")}, Scholarship {rb.Stats.GetValueOrDefault("scholarship")}, Economy {rb.Stats.GetValueOrDefault("economy")}");

        var rep = ComputeReputationScore();
        var visitorFlow = Math.Max(0, rb.Stats.GetValueOrDefault("hospitality") + rb.Stats.GetValueOrDefault("sanctity") + rep / 2);
        var incidentRisk = Math.Max(1, 14 - rb.Stats.GetValueOrDefault("defense") - rb.Stats.GetValueOrDefault("stability") - Virtue("temperance"));
        lines.Add($"Derived: VisitorFlow {visitorFlow}, DonationRate baseline {Math.Max(1, visitorFlow / 2)}, IncidentRisk d20≤{incidentRisk}.");

        if (rb.ActiveProject is null)
        {
            lines.Add("Active Project: none");
        }
        else
        {
            lines.Add($"Active Project: {ResolveNodeLabel(rb.ActiveProject.NodeId)} L{rb.ActiveProject.TargetLevel} ({rb.ActiveProject.DaysRemaining} day(s) remaining, labor/day {rb.ActiveProject.RequiredLaborPerDay}).");
        }

        lines.Add($"Labor: monks {rb.LaborAssigned.GetValueOrDefault("monks")}, lay brothers {rb.LaborAssigned.GetValueOrDefault("laybrothers")}, hired workers {rb.LaborAssigned.GetValueOrDefault("workers")}, stress {rb.LaborStress}.");
        lines.Add($"Visitors today: {rb.VisitorsToday}/{rb.VisitorCapacity}. Lifetime donations generated by hosting: {rb.DonationsTotal}d.");
        lines.Add($"Collections: Books {ComputeCollectionPercent("book")}% | Relics {ComputeCollectionPercent("relic")}%. ");
        lines.Add("Use 'rebuild plan' to inspect upgrade trees.");
    }

    private void RenderRebuildPlan(List<string> lines)
    {
        EnsureRebuildState();
        if (_story.RebuildNodes.Count == 0)
        {
            lines.Add("No rebuild node data is loaded.");
            return;
        }

        lines.Add("Build Menu (node -> next level)");
        foreach (var node in _story.RebuildNodes.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var currentLevel = _state.Rebuild.NodeLevels.GetValueOrDefault(node.NodeId);
            var next = node.Levels.OrderBy(l => l.Level).FirstOrDefault(l => l.Level == currentLevel + 1);
            if (next is null)
            {
                lines.Add($"- {node.Name}: complete (level {currentLevel}/{node.Levels.Count}).");
                continue;
            }

            var cost = string.Join(", ", next.Cost.Select(kv => kv.Key.Equals("coin", StringComparison.OrdinalIgnoreCase) ? $"{kv.Value}d" : $"{kv.Key} x{kv.Value}"));
            var deltas = string.Join(", ", next.StatDelta.Select(kv => $"{kv.Key}+{kv.Value}"));
            lines.Add($"- {node.Name}: next L{next.Level} '{next.Name}' | {next.TimeDays} day(s), labor/day {next.LaborPerDay} | Cost: {cost} | Stats: {deltas}");
        }
        lines.Add("Command: rebuild upgrade <node name or id>");
    }

    private void AssignLaborPreset(string preset, List<string> lines)
    {
        EnsureRebuildState();
        var p = preset.Trim().ToLowerInvariant();
        switch (p)
        {
            case "aggressive":
                _state.Rebuild.LaborAssigned["monks"] = 3;
                _state.Rebuild.LaborAssigned["laybrothers"] = 2;
                _state.Rebuild.LaborAssigned["workers"] = 2;
                _state.Rebuild.LaborStress = Math.Clamp(_state.Rebuild.LaborStress + 2, 0, 20);
                lines.Add("Labor preset set to aggressive: fast progress, higher stress risk.");
                return;
            case "cautious":
                _state.Rebuild.LaborAssigned["monks"] = 1;
                _state.Rebuild.LaborAssigned["laybrothers"] = 1;
                _state.Rebuild.LaborAssigned["workers"] = 0;
                _state.Rebuild.LaborStress = Math.Clamp(_state.Rebuild.LaborStress - 1, 0, 20);
                lines.Add("Labor preset set to cautious: slower progress, lower stress.");
                return;
            default:
                _state.Rebuild.LaborAssigned["monks"] = 2;
                _state.Rebuild.LaborAssigned["laybrothers"] = 1;
                _state.Rebuild.LaborAssigned["workers"] = 1;
                lines.Add("Labor preset set to balanced.");
                return;
        }
    }

    private void StartRebuildUpgrade(string nodeQuery, List<string> lines)
    {
        EnsureRebuildState();
        if (_state.Rebuild.ActiveProject is not null)
        {
            lines.Add("A project is already underway. Complete it or wait for completion before starting another.");
            return;
        }

        if (!TryResolveRebuildNode(nodeQuery, out var node))
        {
            lines.Add("Unknown node. Use 'rebuild plan' to see available nodes.");
            return;
        }

        var currentLevel = _state.Rebuild.NodeLevels.GetValueOrDefault(node!.NodeId);
        var level = node.Levels.OrderBy(l => l.Level).FirstOrDefault(l => l.Level == currentLevel + 1);
        if (level is null)
        {
            lines.Add($"{node.Name} is already at maximum level.");
            return;
        }

        if (!CheckRebuildGating(node, level, lines))
            return;

        if (!TryPayRebuildCost(level.Cost, lines))
            return;

        _state.Rebuild.ActiveProject = new ActiveRebuildProject
        {
            NodeId = node.NodeId,
            TargetLevel = level.Level,
            DaysRemaining = level.TimeDays,
            RequiredLaborPerDay = Math.Max(1, level.LaborPerDay)
        };

        lines.Add($"Project started: {node.Name} L{level.Level} - {level.Name}. Estimated {level.TimeDays} day(s).");
        if (level.Unlocks.Count > 0)
            lines.Add("On completion unlocks: " + string.Join("; ", level.Unlocks));
    }

    private bool CheckRebuildGating(RebuildNodeDef node, RebuildLevelDef level, List<string> lines)
    {
        foreach (var req in node.RequiresNodeLevels)
        {
            var have = _state.Rebuild.NodeLevels.GetValueOrDefault(req.Key);
            if (have < req.Value)
            {
                lines.Add($"Locked: requires {ResolveNodeLabel(req.Key)} level {req.Value}.");
                return false;
            }
        }

        foreach (var statReq in node.MinStats)
        {
            var have = _state.Rebuild.Stats.GetValueOrDefault(statReq.Key);
            if (have < statReq.Value)
            {
                lines.Add($"Locked: requires {statReq.Key} {statReq.Value} (current {have}).");
                return false;
            }
        }

        return true;
    }

    private bool TryPayRebuildCost(Dictionary<string, int> cost, List<string> lines)
    {
        var materialNeed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var coinNeed = 0;
        foreach (var kv in cost)
        {
            if (kv.Key.Equals("coin", StringComparison.OrdinalIgnoreCase)) coinNeed += kv.Value;
            else materialNeed[kv.Key] = kv.Value;
        }

        if (_state.Coin < coinNeed)
        {
            lines.Add($"Insufficient coin: need {coinNeed}d, have {_state.Coin}d.");
            return false;
        }

        foreach (var need in materialNeed)
        {
            var have = CountMaterial(need.Key);
            if (have < need.Value)
            {
                lines.Add($"Insufficient {need.Key}: need {need.Value}, have {have}.");
                return false;
            }
        }

        _state.Coin -= coinNeed;
        foreach (var need in materialNeed)
            RemoveMaterial(need.Key, need.Value);

        lines.Add($"Construction cost paid: {coinNeed}d and {string.Join(", ", materialNeed.Select(kv => $"{kv.Key} x{kv.Value}"))}.");
        return true;
    }

    private bool TryResolveRebuildNode(string query, out RebuildNodeDef? node)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(query)) return false;
        var q = query.Trim();

        node = _story.RebuildNodes.Values.FirstOrDefault(n => n.NodeId.Equals(q, StringComparison.OrdinalIgnoreCase)
            || n.Name.Equals(q, StringComparison.OrdinalIgnoreCase)
            || n.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || n.NodeId.Contains(q, StringComparison.OrdinalIgnoreCase));

        return node is not null;
    }

    private string ResolveNodeLabel(string nodeId)
        => _story.RebuildNodes.TryGetValue(nodeId, out var node) ? node.Name : nodeId;

    private int CountMaterial(string materialKey)
    {
        var target = NormalizeMaterialName(materialKey);
        return _state.Inventory.Count(i => NormalizeMaterialName(i) == target);
    }

    private void RemoveMaterial(string materialKey, int count)
    {
        var target = NormalizeMaterialName(materialKey);
        for (var i = _state.Inventory.Count - 1; i >= 0 && count > 0; i--)
        {
            if (NormalizeMaterialName(_state.Inventory[i]) != target) continue;
            _state.Inventory.RemoveAt(i);
            count--;
        }
    }

    private static string NormalizeMaterialName(string raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);

    private int ComputeCollectionPercent(string group)
    {
        var g = group.ToLowerInvariant();
        var count = _state.Inventory.Count(i => i.Contains(g, StringComparison.OrdinalIgnoreCase));
        return Math.Min(100, count * 10);
    }

    private void ProcessRebuildDay(List<string> lines)
    {
        EnsureRebuildState();
        ProcessProjectProgress(lines);
        ProcessVisitorsAndDonations(lines);
        ProcessConstructionComplications(lines);
    }

    private void ProcessProjectProgress(List<string> lines)
    {
        var active = _state.Rebuild.ActiveProject;
        if (active is null) return;

        var labor = _state.Rebuild.LaborAssigned.GetValueOrDefault("monks") * 2
            + _state.Rebuild.LaborAssigned.GetValueOrDefault("laybrothers") * 2
            + _state.Rebuild.LaborAssigned.GetValueOrDefault("workers")
            + Math.Max(0, _state.Rebuild.Stats.GetValueOrDefault("economy"));

        if (labor >= active.RequiredLaborPerDay)
            active.DaysRemaining -= 1;
        else if (_rng.Next(0, 100) < 35)
            active.DaysRemaining += 1;

        if (active.DaysRemaining > 0)
        {
            lines.Add($"Rebuild progress: {ResolveNodeLabel(active.NodeId)} needs {active.DaysRemaining} more day(s).");
            return;
        }

        if (!_story.RebuildNodes.TryGetValue(active.NodeId, out var node))
        {
            _state.Rebuild.ActiveProject = null;
            return;
        }

        var level = node.Levels.FirstOrDefault(l => l.Level == active.TargetLevel);
        if (level is null)
        {
            _state.Rebuild.ActiveProject = null;
            return;
        }

        _state.Rebuild.NodeLevels[node.NodeId] = active.TargetLevel;
        foreach (var delta in level.StatDelta)
            _state.Rebuild.Stats[delta.Key.ToLowerInvariant()] = _state.Rebuild.Stats.GetValueOrDefault(delta.Key.ToLowerInvariant()) + delta.Value;

        _state.Rebuild.VisitorCapacity = 4 + _state.Rebuild.Stats.GetValueOrDefault("hospitality");
        _state.Rebuild.ActiveProject = null;
        lines.Add($"[Project Complete] {node.Name} L{level.Level}: {level.Name}.");
        if (level.Unlocks.Count > 0)
            lines.Add("Unlocked: " + string.Join("; ", level.Unlocks));
    }

    private void ProcessVisitorsAndDonations(List<string> lines)
    {
        var rb = _state.Rebuild;
        var rep = ComputeReputationScore();
        var visitorFlow = Math.Max(0, rb.Stats.GetValueOrDefault("hospitality") + rb.Stats.GetValueOrDefault("sanctity") + rep / 2);
        var visitors = Math.Clamp(visitorFlow / 2 + _rng.Next(0, 3), 0, rb.VisitorCapacity + 3);
        rb.VisitorsToday = Math.Min(visitors, rb.VisitorCapacity);
        var overflow = Math.Max(0, visitors - rb.VisitorCapacity);
        if (overflow > 0)
            lines.Add($"Visitor overflow: {overflow} traveler(s) could not be lodged.");

        var donation = rb.VisitorsToday * (1 + rb.Stats.GetValueOrDefault("hospitality") / 3)
            + Math.Max(0, Virtue("humility"))
            + Math.Max(0, Virtue("temperance"));
        if (Virtue("charity") >= 4)
            donation = Math.Max(0, donation - 1);

        if (donation > 0)
        {
            _state.Coin += donation;
            rb.DonationsTotal += donation;
            lines.Add($"Hosting yields {donation}d in gifts and patron support.");
        }
    }

    private void ProcessConstructionComplications(List<string> lines)
    {
        var active = _state.Rebuild.ActiveProject;
        if (active is null) return;

        var incidentRiskTarget = Math.Max(1, 14 - _state.Rebuild.Stats.GetValueOrDefault("defense") - _state.Rebuild.Stats.GetValueOrDefault("stability") - Virtue("temperance"));
        if (_rng.Next(1, 21) > incidentRiskTarget) return;

        var complication = _rng.Next(0, 3);
        switch (complication)
        {
            case 0:
                lines.Add("Complication: a storm tears part of the scaffolding.");
                if (CountMaterial("logs") > 0)
                {
                    RemoveMaterial("logs", 1);
                    lines.Add("You consume 1 Logs to patch quickly and keep the schedule.");
                }
                else
                {
                    active.DaysRemaining += 1;
                    lines.Add("No spare timber; project delayed by 1 day.");
                }
                break;
            case 1:
                lines.Add("Complication: worker injury on site.");
                if (_state.Coin >= 4)
                {
                    _state.Coin -= 4;
                    lines.Add("You pay the healer (4d), avoiding stoppage.");
                }
                else
                {
                    _state.Rebuild.LaborStress = Math.Clamp(_state.Rebuild.LaborStress + 2, 0, 20);
                    active.DaysRemaining += 1;
                    lines.Add("Without coin for quick treatment, morale and pace dip (delay +1 day).");
                }
                break;
            default:
                lines.Add("Complication: diocesan inspection requests records and witness.");
                var check = Virtue("humility") + Virtue("faith") + _state.Rebuild.Stats.GetValueOrDefault("sanctity") + _rng.Next(1, 7);
                if (check >= 8)
                {
                    lines.Add("Inspection clears with minimal disruption.");
                    _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 1, 0, 100);
                }
                else
                {
                    lines.Add("Inspection finds irregularities; paperwork slows labor (+1 day).");
                    active.DaysRemaining += 1;
                }
                break;
        }
    }

    private void OpenShop(string shopId, List<string> lines)
    {
        if (!_story.Shops.TryGetValue(shopId, out var shop))
        {
            lines.Add("Shop data missing.");
            return;
        }

        lines.Add($"{shop.Name}: {shop.Description}");
        var idx = 1;
        foreach (var stock in shop.Stock)
        {
            if (!_story.Items.TryGetValue(stock.ItemId, out var item)) continue;
            lines.Add($"  [{idx}] {item.Name} - {FormatSterling(stock.Price)} ({item.Description})");
            idx++;
        }
        lines.Add("Type the shop action again using its purchase menu to buy specific goods.");
    }

    private void CheckProgress(List<string> lines)
    {
        var total = _state.Counters.GetValueOrDefault("task_total");

        if (total >= 8 && !_state.Flags.Contains("arc_village"))
        {
            SetWorldFlag("arc_village");
            StartQuest("village_petitions", lines);
            lines.Add("Word spreads through Blackpine: the priory's labors are changing village life. New disputes and petitions arrive.");
            return;
        }

        if (total >= 16 && !_state.Flags.Contains("arc_orders"))
        {
            SetWorldFlag("arc_orders");
            StartQuest("orders_concord", lines);
            lines.Add("Franciscans, Carmelite travelers, and local Benedictine agents each seek influence in Blackpine.");
            return;
        }

        if (total >= 24 && !_state.Flags.Contains("arc_york"))
        {
            SetWorldFlag("arc_york");
            StartQuest("york_deputation", lines);
            lines.Add("A Dominican courier arrives from York with letters on doctrine, debt, and disorder. The stakes rise.");
            return;
        }

        if (total >= 32 && !_state.Flags.Contains("arc_longwinter"))
        {
            SetWorldFlag("arc_longwinter");
            StartQuest("winter_mercy", lines);
            lines.Add("A hard winter sets in. Supplies tighten, rumors multiply, and Saint Catherine must decide what to protect first.");
            return;
        }

        if (total >= 40 && !_state.Flags.Contains("arc_final"))
        {
            SetWorldFlag("arc_final");
            lines.Add("The first great rebuilding cycle is complete. The priory now faces consequences of everything you have chosen.");
            return;
        }

        if (total >= 48 && !_state.Flags.Contains("arc_avignon"))
        {
            SetWorldFlag("arc_avignon");
            StartQuest("avignon_echoes", lines);
            lines.Add("Sealed letters tied to Avignon patronage arrive with elegant phrasing and perilous conditions.");
            return;
        }

        if (total >= 56 && !_state.Flags.Contains("arc_bohemia"))
        {
            SetWorldFlag("arc_bohemia");
            StartQuest("bohemian_spark", lines);
            lines.Add("Travelers carry troubling reports from Prague: controversy now rides rumor roads faster than carts.");
            return;
        }

        if (total >= 64 && !_state.Flags.Contains("arc_cloth"))
        {
            SetWorldFlag("arc_cloth");
            StartQuest("cloth_and_candle", lines);
            lines.Add("Wool and candle prices convulse; guild delegates now court Saint Catherine with polished promises.");
            return;
        }

        if (total >= 72 && !_state.Flags.Contains("arc_shells"))
        {
            SetWorldFlag("arc_shells");
            StartQuest("road_of_shells", lines);
            lines.Add("Pilgrim bands begin to pass through Blackpine, forcing charity and logistics into the same narrow doorway.");
            return;
        }

        if (total >= 80 && !_state.Flags.Contains("arc_border"))
        {
            SetWorldFlag("arc_border");
            StartQuest("border_of_ash", lines);
            lines.Add("A daughter-house near the northern marches begs aid: medicine, mediation, and a steady preacher.");
            return;
        }

        if (total >= 88 && !_state.Flags.Contains("arc_sealed_room"))
        {
            SetWorldFlag("arc_sealed_room");
            StartQuest("sealed_room", lines);
            lines.Add("An internal breach at Saint Catherine forces discipline, truth, and mercy into painful collision.");
            return;
        }

        lines.Add("The long arc continues to gather quietly.");
    }

    private void GateArc(string flag, string sceneId, string fallback, List<string> lines)
    {
        if (_state.Flags.Contains(flag))
        {
            _state.SceneId = sceneId;
            lines.Add(CurrentScene().Text);
            lines.Add(ExitLine(CurrentScene()));
            AddContextTip(lines);
        }
        else
        {
            lines.Add(fallback);
        }
    }

    private void ResolveEndgame(List<string> lines)
    {
        var score = _state.Priory["food"] + _state.Priory["morale"] + _state.Priory["piety"] + _state.Priory["security"] + _state.Priory["relations"];
        if (score >= 350)
            lines.Add("Saint Catherine is renewed: chapel, school, infirmary, and alms-house all endure. Your governance joined truth with mercy.");
        else if (score >= 290)
            lines.Add("Saint Catherine survives with scars. Some holdings are lost, but the priory remains a living witness in Blackpine.");
        else
            lines.Add("Saint Catherine is diminished under debt and pressure. Yet fragments of doctrine, charity, and memory remain for another generation.");

        CompleteQuest("main_rebuild_priory", lines);
        IsEnded = true;
    }

    private void ExecuteTask(string type, List<string> lines)
    {
        switch (type)
        {
            case "sermon":
                _state.Counters["task_sermon"] = _state.Counters.GetValueOrDefault("task_sermon") + 1;
                _state.Counters["task_total"] = _state.Counters.GetValueOrDefault("task_total") + 1;
                _state.Priory["piety"] = Math.Clamp(_state.Priory["piety"] + 2, 0, 100);
                _state.Priory["relations"] = Math.Clamp(_state.Priory["relations"] + 1, 0, 100);
                lines.Add(Pick(
                    "You preach at Blackpine's edge: mercy without softness, truth without cruelty.",
                    "In the market lane, you answer questions on confession, debt, and conscience until dusk.",
                    "At a roadside chapel, your sermon turns a brewing feud away from violence."));
                break;
            case "study":
                _state.Counters["task_study"] = _state.Counters.GetValueOrDefault("task_study") + 1;
                _state.Counters["task_total"] = _state.Counters.GetValueOrDefault("task_total") + 1;
                AddVirtue("hope", 1);
                AddVirtue("humility", 1);
                _state.Priory["piety"] = Math.Clamp(_state.Priory["piety"] + 1, 0, 100);
                lines.Add(Pick(
                    "You copy disputed texts with Dominican annotations and sharpen your judgment.",
                    "Brother Martin drills you in logic and pastoral casuistry until night bells.",
                    "You draft guidance on contracts and usury for Blackpine's guild elders."));
                break;
            case "patrol":
                _state.Counters["task_patrol"] = _state.Counters.GetValueOrDefault("task_patrol") + 1;
                _state.Counters["task_total"] = _state.Counters.GetValueOrDefault("task_total") + 1;
                _state.Priory["security"] = Math.Clamp(_state.Priory["security"] + 2, 0, 100);
                AddVirtue("fortitude", 1);
                lines.Add(Pick(
                    "You walk the forest road at vespers. Trouble recedes before discipline.",
                    "At the mill bend, you prevent a fight before knives clear sheaths.",
                    "You escort nuns returning from infirmary service through dangerous timber routes."));
                break;
            case "fields":
                _state.Counters["task_fields"] = _state.Counters.GetValueOrDefault("task_fields") + 1;
                _state.Counters["task_total"] = _state.Counters.GetValueOrDefault("task_total") + 1;
                _state.Priory["food"] = Math.Clamp(_state.Priory["food"] + 2, 0, 100);
                _state.Priory["treasury"] = Math.Clamp(_state.Priory["treasury"] + 1, 0, 100);
                lines.Add(Pick(
                    "You reorganize stores and catch theft in the tally before it ruins the week.",
                    "You settle a boundary dispute and preserve both harvest and peace.",
                    "You spend a wet day mending ditches; by evening the lower field drains cleanly."));
                break;
            case "charity":
                _state.Counters["task_charity"] = _state.Counters.GetValueOrDefault("task_charity") + 1;
                _state.Counters["task_total"] = _state.Counters.GetValueOrDefault("task_total") + 1;
                _state.Priory["morale"] = Math.Clamp(_state.Priory["morale"] + 2, 0, 100);
                _state.Priory["relations"] = Math.Clamp(_state.Priory["relations"] + 2, 0, 100);
                _state.Priory["treasury"] = Math.Clamp(_state.Priory["treasury"] - 1, 0, 100);
                lines.Add(Pick(
                    "You distribute bread, lamp oil, and medical herbs with disciplined compassion.",
                    "A fevered child survives after coordinated care between priory brothers and local sisters.",
                    "You mediate a crushing marriage debt and prevent a family collapse."));
                break;
        }

        AdvanceTime(lines, 1);
    }

    private void StartQuest(string questId, List<string> lines)
    {
        if (!_story.Quests.TryGetValue(questId, out var q)) return;
        if (_state.CompletedQuests.Contains(questId) || _state.ActiveQuests.Contains(questId)) return;

        var memberCount = _partyState?.Members.Count ?? 1;
        if (q.MinPartySize > memberCount)
        {
            lines.Add($"[Quest Locked] {q.Title} requires at least {q.MinPartySize} companions in party.");
            return;
        }

        _state.ActiveQuests.Add(questId);
        lines.Add($"[Quest Started] {q.Title}: {q.Description}");
        if (q.RequiresSynchronizedParty)
            lines.Add("[Co-op Hook] This quest can later enforce synchronized real-time party participation.");
    }

    private void CompleteQuest(string questId, List<string> lines)
    {
        if (!_state.ActiveQuests.Contains(questId)) return;
        _state.ActiveQuests.Remove(questId);
        _state.CompletedQuests.Add(questId);
        if (_story.Quests.TryGetValue(questId, out var q))
            lines.Add($"[Quest Completed] {q.Title}");
    }

    private string QuestLog()
    {
        var lines = new List<string>();
        lines.Add("Active Quests:");
        if (_state.ActiveQuests.Count == 0) lines.Add("  (none)");
        foreach (var q in _state.ActiveQuests.OrderBy(x => x))
        {
            if (_story.Quests.TryGetValue(q, out var def)) lines.Add($"  - {def.Title}: {def.Description}");
            else lines.Add($"  - {q}");
        }

        lines.Add("Completed Quests:");
        if (_state.CompletedQuests.Count == 0) lines.Add("  (none)");
        foreach (var q in _state.CompletedQuests.OrderBy(x => x))
        {
            if (_story.Quests.TryGetValue(q, out var def)) lines.Add($"  - {def.Title}");
            else lines.Add($"  - {q}");
        }

        return string.Join("\n", lines);
    }


    private void PlayMiniGame(string name, List<string> lines)
    {
        switch (name)
        {
            case "tavern_dice":
                PlayTavernDice(lines);
                return;
            case "rosary":
                PrayRosary(lines);
                return;
            case "fishing":
                GoFishing(lines);
                return;
            case "investigate":
                PlayInvestigationBoard(lines);
                return;
            case "ledger":
                PlayLedgerPuzzle(lines);
                return;
            case "herbal":
                PlayHerbalRemedy(lines);
                return;
            case "hunt":
                PlayTrackingHunt(lines);
                return;
            case "woodcut":
                PlayWoodcutHaul(lines);
                return;
            case "masonry":
                PlayMasonryPlanning(lines);
                return;
            case "sermon":
                PlaySermonCraft(lines);
                return;
            case "queue":
                PlayCrowdQueueControl(lines);
                return;
            case "dispute":
                PlayScholasticDisputation(lines);
                return;
            case "alms":
                PlayAlmsTriage(lines);
                return;
            case "stealth":
                PlayNightWatchPatrol(lines);
                return;
            default:
                lines.Add("That pastime is not available.");
                return;
        }
    }

    private void PlayTavernDice(List<string> lines)
    {
        if (_state.Coin <= 0)
        {
            lines.Add("You have no coin to wager at the table.");
            return;
        }

        var wager = Math.Clamp(_rng.Next(1, 4), 1, _state.Coin);
        var rounds = _rng.Next(2, 5);
        var wins = 0;
        var losses = 0;

        lines.Add($"You sit for tavern dice: {rounds} rounds, {wager} pennies at risk each round.");

        for (var r = 1; r <= rounds; r++)
        {
            var yourRoll = _rng.Next(1, 7) + _rng.Next(1, 7) + (Virtue("temperance") > 2 ? 1 : 0);
            var houseRoll = _rng.Next(1, 7) + _rng.Next(1, 7);
            if (yourRoll >= houseRoll)
            {
                wins++;
                lines.Add($"Round {r}: {yourRoll} vs {houseRoll} — you take the pot.");
            }
            else
            {
                losses++;
                lines.Add($"Round {r}: {yourRoll} vs {houseRoll} — the house takes it.");
            }
        }

        var net = (wins - losses) * wager;
        _state.Coin += net;

        if (net > 0)
        {
            _state.Priory["morale"] = Math.Clamp(_state.Priory["morale"] + 1, 0, 100);
            lines.Add($"You leave up {net} pennies and with a little local goodwill.");
        }
        else if (net < 0)
        {
            _state.Virtues["temperance"] = Math.Max(Virtue("temperance") - 1, -10);
            lines.Add($"You lose {Math.Abs(net)} pennies. A costly lesson in appetite.");
        }
        else
        {
            lines.Add("You break even. Not triumph, not ruin.");
        }

        AdvanceTime(lines, 1);
    }

    private void PrayRosary(List<string> lines)
    {
        var mysteries = new[] { "Joyful", "Sorrowful", "Glorious" };
        var chosen = mysteries[_rng.Next(mysteries.Length)];
        var focus = 0;
        lines.Add($"You pray a {chosen} Rosary with the friars.");

        for (var decade = 1; decade <= 5; decade++)
        {
            var recollection = _rng.Next(1, 7) + Math.Max(0, Virtue("faith")) + Math.Max(0, Virtue("temperance"));
            var distraction = _rng.Next(1, 9) + (_state.Day > 20 ? 1 : 0);
            if (recollection >= distraction)
            {
                focus++;
                lines.Add($"Decade {decade}: recollection holds.");
            }
            else
            {
                lines.Add($"Decade {decade}: your mind wanders, then returns.");
            }
        }

        _state.Priory["piety"] = Math.Clamp(_state.Priory["piety"] + 1 + (focus / 2), 0, 100);
        _state.Priory["morale"] = Math.Clamp(_state.Priory["morale"] + (focus >= 3 ? 1 : 0), 0, 100);
        AddVirtue("temperance", 1);
        AddVirtue("faith", 1);
        if (focus >= 4) AddVirtue("hope", 1);

        lines.Add(focus >= 4
            ? "Prayer steadies your judgment for the day."
            : "Prayer gives enough peace to continue faithfully.");

        AdvanceTime(lines, 1);
    }

    private void GoFishing(List<string> lines)
    {
        var attempts = 3 + Math.Max(0, Virtue("fortitude")/3);
        var catchCount = 0;
        var fishNames = new[] { "trout", "perch", "pike", "grayling" };

        if (_state.Inventory.Contains("Field Tool Set"))
            attempts += 1;

        lines.Add($"You fish the cold water for {attempts} attempts.");

        for (var i = 0; i < attempts; i++)
        {
            var bite = _rng.Next(1, 11);
            if (bite >= 6)
            {
                catchCount++;
                lines.Add($"Attempt {i + 1}: you land a {fishNames[_rng.Next(fishNames.Length)]}.");
            }
            else
            {
                lines.Add($"Attempt {i + 1}: no strike.");
            }
        }

        if (catchCount > 0)
        {
            var foodGain = Math.Min(4, catchCount);
            _state.Priory["food"] = Math.Clamp(_state.Priory["food"] + foodGain, 0, 100);
            _state.Coin += catchCount;
            lines.Add($"You return with {catchCount} fish. Priory food +{foodGain}; coin +{catchCount} from surplus sale.");
        }
        else
        {
            lines.Add("You return empty-handed, but with clearer eyes.");
            AddVirtue("temperance", 1);
        }

        AdvanceTime(lines, 1);
    }


    private void PlayInvestigationBoard(List<string> lines)
    {
        lines.Add("You pin clues to the board, compare testimony, and test one hypothesis before chapter bell.");
        var clarity = _rng.Next(1, 21) + Virtue("humility") + Virtue("temperance");
        _state.Inventory.Add("Witness Statement");
        if (!_state.Inventory.Contains("Ledger Page")) _state.Inventory.Add("Ledger Page");
        if (clarity >= 18)
        {
            _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 1, 0, 100);
            AddVirtue("humility", 1);
            lines.Add("Your board holds together under scrutiny. Relations +1.");
        }
        else
        {
            lines.Add("Some contradictions remain unresolved; the case can continue next session.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayLedgerPuzzle(List<string> lines)
    {
        lines.Add("You reconcile entries against stores while the steward watches the totals.");
        var score = _rng.Next(1, 21) + Virtue("temperance") + Virtue("humility");
        if (!_state.Inventory.Contains("Audit Stamp")) _state.Inventory.Add("Audit Stamp");
        if (!_state.Inventory.Contains("Inventory Token")) _state.Inventory.Add("Inventory Token");
        if (score >= 17)
        {
            _state.Coin += 2;
            _state.Priory["treasury"] = Math.Clamp(_state.Priory.GetValueOrDefault("treasury") + 1, 0, 100);
            lines.Add("Your accounts reconcile cleanly. Coin +2, treasury +1.");
        }
        else
        {
            _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") - 1, 0, 100);
            lines.Add("A discrepancy remains and confidence dips. Morale -1.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayHerbalRemedy(List<string> lines)
    {
        lines.Add("You sort herbs by scent and bruise, then prepare a careful remedy.");
        if (!_state.Inventory.Contains("Dried Yarrow")) _state.Inventory.Add("Dried Yarrow");
        if (!_state.Inventory.Contains("Comfrey Bundle")) _state.Inventory.Add("Comfrey Bundle");
        var success = _rng.Next(1, 21) + Virtue("charity") + Virtue("faith") >= 16;
        if (success)
        {
            if (!_state.Inventory.Contains("Poultice")) _state.Inventory.Add("Poultice");
            if (!_state.Inventory.Contains("Tincture")) _state.Inventory.Add("Tincture");
            _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") + 1, 0, 100);
            lines.Add("The remedy takes hold. Morale +1 and medicine stocked.");
        }
        else
        {
            lines.Add("The brew is weak this time; materials are consumed for limited relief.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayTrackingHunt(List<string> lines)
    {
        lines.Add("You follow broken brush and hoof grooves, choosing speed over certainty.");
        var score = _rng.Next(1, 21) + Virtue("fortitude") + Virtue("hope");
        if (score >= 17)
        {
            if (!_state.Inventory.Contains("Meat Rations")) _state.Inventory.Add("Meat Rations");
            if (!_state.Inventory.Contains("Wolf Pelt")) _state.Inventory.Add("Wolf Pelt");
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 1, 0, 100);
            lines.Add("You return with proof and provisions. Security +1.");
        }
        else
        {
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") - 1, 0, 100);
            lines.Add("Tracks scatter at dusk; the road remains uneasy. Security -1.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayWoodcutHaul(List<string> lines)
    {
        lines.Add("You balance felling, hauling, and preserving what should not be stripped bare.");
        if (!_state.Inventory.Contains("Logs")) _state.Inventory.Add("Logs");
        if (!_state.Inventory.Contains("Timber Planks")) _state.Inventory.Add("Timber Planks");
        if (!_state.Inventory.Contains("Charcoal Sack")) _state.Inventory.Add("Charcoal Sack");
        _state.Priory["treasury"] = Math.Clamp(_state.Priory.GetValueOrDefault("treasury") + 1, 0, 100);
        AddVirtue("temperance", 1);
        lines.Add("Materials delivered with minimal waste. Treasury +1.");
        AdvanceTime(lines, 1);
    }

    private void PlayMasonryPlanning(List<string> lines)
    {
        lines.Add("You place labor and stone against weak joints before weather can find them.");
        if (!_state.Inventory.Contains("Cut Stone")) _state.Inventory.Add("Cut Stone");
        if (!_state.Inventory.Contains("Lime Mortar")) _state.Inventory.Add("Lime Mortar");
        if (!_state.Inventory.Contains("Iron Nails")) _state.Inventory.Add("Iron Nails");
        var score = _rng.Next(1, 21) + Virtue("temperance") + Virtue("fortitude");
        if (score >= 17)
        {
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 2, 0, 100);
            lines.Add("Your plan holds through inspection. Security +2.");
        }
        else
        {
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 1, 0, 100);
            lines.Add("The plan is serviceable but costly; repairs will need another pass.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlaySermonCraft(List<string> lines)
    {
        lines.Add("You draft theme, tone, and rebuke level, then deliver before a divided crowd.");
        if (!_state.Inventory.Contains("Rumor (intel)")) _state.Inventory.Add("Rumor (intel)");
        if (!_state.Inventory.Contains("Donation Coin")) _state.Inventory.Add("Donation Coin");
        var score = _rng.Next(1, 21) + Virtue("faith") + Virtue("humility");
        if (score >= 17)
        {
            _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 2, 0, 100);
            lines.Add("Your words steady both conscience and temper. Relations +2.");
        }
        else
        {
            _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 1, 0, 100);
            lines.Add("Some are moved, others resist; tension softens only slightly. Relations +1.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayCrowdQueueControl(List<string> lines)
    {
        lines.Add("You assign ushers, triage urgency, and keep the line from turning into panic.");
        if (!_state.Inventory.Contains("Order Token")) _state.Inventory.Add("Order Token");
        if (!_state.Inventory.Contains("Blessed Cloth")) _state.Inventory.Add("Blessed Cloth");
        var score = _rng.Next(1, 21) + Virtue("temperance") + Virtue("charity");
        if (score >= 16)
        {
            _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 1, 0, 100);
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 1, 0, 100);
            lines.Add("Fair ordering prevents a crush and earns trust. Relations +1, security +1.");
        }
        else
        {
            lines.Add("You prevent the worst, but tempers flare and rumors linger.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayScholasticDisputation(List<string> lines)
    {
        lines.Add("Thesis, objection, reply: each exchange tests clarity more than volume.");
        if (!_state.Inventory.Contains("Reference Manuscript")) _state.Inventory.Add("Reference Manuscript");
        if (!_state.Inventory.Contains("Scholar’s Note")) _state.Inventory.Add("Scholar’s Note");
        if (!_state.Inventory.Contains("Seal of Approval")) _state.Inventory.Add("Seal of Approval");
        var score = _rng.Next(1, 21) + Virtue("faith") + Virtue("humility");
        if (score >= 18)
        {
            AddVirtue("faith", 1);
            AddVirtue("humility", 1);
            _state.Priory["piety"] = Math.Clamp(_state.Priory.GetValueOrDefault("piety") + 1, 0, 100);
            lines.Add("You win by precision without pride. Piety +1.");
        }
        else
        {
            lines.Add("The exchange remains unresolved, but no fracture follows.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayAlmsTriage(List<string> lines)
    {
        lines.Add("You triage requests with thin stores: immediate hunger, long-term labor, and fairness all compete.");
        if (!_state.Inventory.Contains("Ration Card")) _state.Inventory.Add("Ration Card");
        if (!_state.Inventory.Contains("Favors (town)")) _state.Inventory.Add("Favors (town)");
        var score = _rng.Next(1, 21) + Virtue("charity") + Virtue("temperance");
        if (score >= 17)
        {
            _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 2, 0, 100);
            _state.Priory["treasury"] = Math.Clamp(_state.Priory.GetValueOrDefault("treasury") - 1, 0, 100);
            lines.Add("The town is fed without complete disorder. Relations +2, treasury -1.");
        }
        else
        {
            _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 1, 0, 100);
            _state.Priory["treasury"] = Math.Clamp(_state.Priory.GetValueOrDefault("treasury") - 2, 0, 100);
            lines.Add("Aid reaches many, but reserves take a heavy hit. Relations +1, treasury -2.");
        }
        AdvanceTime(lines, 1);
    }

    private void PlayNightWatchPatrol(List<string> lines)
    {
        lines.Add("You walk dark corridors and gate paths, checking locks before rumor picks a culprit.");
        if (!_state.Inventory.Contains("Recovered Tools")) _state.Inventory.Add("Recovered Tools");
        if (!_state.Inventory.Contains("Key Ring")) _state.Inventory.Add("Key Ring");
        if (!_state.Inventory.Contains("Contraband")) _state.Inventory.Add("Contraband");
        var score = _rng.Next(1, 21) + Virtue("fortitude") + Virtue("humility");
        if (score >= 16)
        {
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 2, 0, 100);
            lines.Add("You catch the breach cleanly and avoid false blame. Security +2.");
        }
        else
        {
            _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 1, 0, 100);
            lines.Add("You deter repeat theft, though the full truth remains murky. Security +1.");
        }
        AdvanceTime(lines, 1);
    }

    private string Pick(params string[] lines) => lines[_rng.Next(lines.Length)];

    private static string CanonicalVirtueKey(string key)
    {
        var normalized = (key ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "prudence" => "humility",
            "justice" => "charity",
            _ => normalized
        };
    }

    private int Virtue(string key)
        => _state.Virtues.GetValueOrDefault(CanonicalVirtueKey(key));

    private void AddVirtue(string key, int delta)
    {
        var canonical = CanonicalVirtueKey(key);
        if (string.IsNullOrWhiteSpace(canonical) || delta == 0) return;
        _state.Virtues[canonical] = _state.Virtues.GetValueOrDefault(canonical) + delta;
    }

    private string FormatVirtueChanges(IReadOnlyDictionary<string, int> deltas)
    {
        var parts = deltas
            .Where(kv => kv.Value != 0)
            .Select(kv => (key: CanonicalVirtueKey(kv.Key), kv.Value))
            .Where(kv => !string.IsNullOrWhiteSpace(kv.key))
            .Select(kv => $"{kv.key} {(kv.Value > 0 ? "+" : "")}{kv.Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }

    private static string? BuildVirtueGrantKey(string? sourceContext, MenuOptionDef option)
    {
        if (string.IsNullOrWhiteSpace(sourceContext) || string.IsNullOrWhiteSpace(option.Text)) return null;
        var normalizedText = NormalizePhrase(option.Text).Replace(' ', '_');
        return $"virtue_once:{NormalizePhrase(sourceContext).Replace(' ', '_')}:{normalizedText}";
    }

    private string VirtueDiagram()
    {
        var spec = new (string Key, string Label, string ColorDot)[]
        {
            ("fortitude", "Fortitude", "🔴"),
            ("temperance", "Temperance", "🟢"),
            ("faith", "Faith", "🟣"),
            ("hope", "Hope", "🔵"),
            ("charity", "Charity", "🟡"),
            ("humility", "Humility", "⚪")
        };

        const int maxBars = 10;
        var rows = spec.Select(v =>
        {
            var raw = _state.Virtues.GetValueOrDefault(v.Key);
            var bars = Math.Clamp(raw + 5, 0, maxBars);
            var bar = new string('█', bars) + new string('░', maxBars - bars);
            return $"{v.ColorDot} {v.Label,-10} [{bar}] {raw:+#;-#;0}";
        });

        return "Virtues\n" + string.Join("\n", rows);
    }

    private bool IsCurrentSex(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        return label.Trim().ToLowerInvariant() switch
        {
            "male" or "man" or "m" => _state.Sex == PlayerSex.Male,
            "female" or "woman" or "f" => _state.Sex == PlayerSex.Female,
            _ => false
        };
    }

    private bool ViolatesClericalRoleRestrictions(MenuOptionDef option, out string reason)
    {
        reason = "";
        if (_state.Sex != PlayerSex.Female) return false;

        var text = (option.Text ?? string.Empty).ToLowerInvariant();
        var disallowedPhrases = new[]
        {
            "become a friar",
            "be ordained",
            "ordination",
            "take holy orders",
            "become a priest",
            "become priest",
            "become a deacon",
            "become deacon",
            "receive the diaconate",
            "receive priestly"
        };

        if (disallowedPhrases.Any(text.Contains))
        {
            reason = "that clerical office is not open in this setting";
            return true;
        }

        return false;
    }

    private static string SexLabel(PlayerSex sex) => sex switch
    {
        PlayerSex.Female => "female",
        PlayerSex.Male => "male",
        _ => "unknown"
    };

    private string LifePathFlavor(string optionText)
    {
        if (optionText.StartsWith("Lay Aspirant", StringComparison.OrdinalIgnoreCase))
            return optionText + " (service, prayer, and obedience)";
        if (optionText.StartsWith("Scholar's Son", StringComparison.OrdinalIgnoreCase))
        {
            var label = _state.Sex == PlayerSex.Female
                ? optionText.Replace("Scholar's Son", "Scholar's Daughter", StringComparison.OrdinalIgnoreCase)
                : optionText;
            return label + " (ink, argument, and memory)";
        }
        if (optionText.StartsWith("Former Man-at-Arms", StringComparison.OrdinalIgnoreCase))
        {
            var label = _state.Sex == PlayerSex.Female
                ? optionText.Replace("Man-at-Arms", "Woman-at-Arms", StringComparison.OrdinalIgnoreCase)
                : optionText;
            return label + " (discipline, watchfulness, and scars)";
        }
        if (optionText.StartsWith("Merchant's Apprentice", StringComparison.OrdinalIgnoreCase))
            return optionText + " (bargains, ledgers, and leverage)";
        if (optionText.StartsWith("Farmer's Son", StringComparison.OrdinalIgnoreCase))
        {
            var label = _state.Sex == PlayerSex.Female
                ? optionText.Replace("Farmer's Son", "Farmer's Daughter", StringComparison.OrdinalIgnoreCase)
                : optionText;
            return label + " (soil, seasons, and endurance)";
        }
        return optionText;
    }

    private void AppendMenuWithContext(string menuId, List<string> lines, string? actionKey = null)
    {
        if (!_story.Menus.TryGetValue(menuId, out var menu))
            return;

        var context = BuildMenuContextLine(menuId, actionKey);
        if (!string.IsNullOrWhiteSpace(context))
            lines.Add(context);

        var reminder = BuildMenuChoiceReminder(menuId);
        if (!string.IsNullOrWhiteSpace(reminder))
            lines.Add(reminder);

        lines.Add(RenderMenu(menu));
    }

    private string? BuildMenuContextLine(string menuId, string? actionKey)
    {
        var sceneId = _state.SceneId;
        var trigger = string.IsNullOrWhiteSpace(actionKey) ? "scene" : actionKey.Trim().ToLowerInvariant();
        var seenFlag = $"menu_context_seen:{sceneId}:{menuId}:{trigger.Replace(' ', '_')}";
        if (_state.Flags.Contains(seenFlag))
            return null;

        _state.Flags.Add(seenFlag);

        if (menuId == "franciscan_relief_menu")
            return "A relief appeal has reached you because roads are unsafe, stores are thin, and every delay can cost lives. You are deciding how much risk Saint Catherine absorbs today.";

        if (menuId == "petition_menu")
            return "Petitioners have queued since dawn. Each request competes for the same limited coin, labor, and credibility.";

        if (menuId == "task_board_menu")
            return "The board reflects real shortages and obligations; each task advances one need while leaving another unattended.";

        if (menuId.StartsWith("intro_", StringComparison.OrdinalIgnoreCase))
            return "These first choices shape your spiritual posture before your public duties begin.";

        if (!string.IsNullOrWhiteSpace(actionKey))
            return $"You focus on the {actionKey}. What you choose here can help or burden the people who rely on Saint Catherine.";

        return "You pause to weigh obligations, risks, and who will bear the cost of your decision.";
    }

    private string RenderMenu(MenuDef menu)
    {
        var sb = new StringBuilder();
        sb.AppendLine(menu.Prompt);
        var available = menu.Options.Select((o, i) => (o, i)).Where(x => IsOptionAvailable(x.o, out _, $"menu:{menu.Id}:{x.i}")).Select(x => x.o).ToList();
        for (var i = 0; i < available.Count; i++)
        {
            var optionText = available[i].Text;
            if (menu.Id == "life_path")
                optionText = LifePathFlavor(optionText);
            sb.AppendLine($"{i + 1}) {optionText}");
        }
        if (available.Count == 0)
            sb.AppendLine("(No options currently available.)");
        return sb.ToString();
    }

    private void HandleMovement(ParsedInput parsed, List<string> lines)
    {
        var scene = CurrentScene();
        var exits = AvailableExits(scene);
        var target = parsed.Target;

        if (string.IsNullOrWhiteSpace(target) && (parsed.Verb is "climb" or "board" or "mount") && exits.ContainsKey("cart"))
            target = "cart";
        if (string.IsNullOrWhiteSpace(target) && (parsed.Verb is "leave" or "depart") && exits.ContainsKey("leave cart"))
            target = "leave cart";

        if (IsOutsideRequest(parsed, target) && TryMoveOutside(lines))
        {
            AdvanceTime(lines, 1);
            if (CurrentScene().EndChapter) IsEnded = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            lines.Add("Go where? Try one of: " + (exits.Count == 0 ? "nowhere yet" : string.Join(", ", exits.Keys)));
            return;
        }

        if (!TryResolveKey(exits.Keys, target, out var resolvedExit))
        {
            lines.Add("You cannot travel there from this location.");
            lines.Add("Available routes: " + (exits.Count == 0 ? "none" : string.Join(", ", exits.Keys)));
            return;
        }

        MoveToScene(exits[resolvedExit!], lines);
        AdvanceTime(lines, 1);
        if (CurrentScene().EndChapter) IsEnded = true;
    }

    private void HandleAction(ParsedInput parsed, List<string> lines)
    {
        var scene = CurrentScene();
        var actions = AvailableActions(scene);
        var target = parsed.Target;
        if (string.IsNullOrWhiteSpace(target))
        {
            lines.Add("Be specific. Example: 'talk friar' or 'examine cart'.");
            return;
        }

        if (!TryResolveKey(actions.Keys, target, out var resolvedAction))
        {
            lines.Add("Nothing comes of it.");
            if (actions.Count > 0)
                lines.Add("Try one of: " + string.Join(", ", actions.Keys.Take(5)));
            return;
        }

        var result = actions[resolvedAction!];

        if (parsed.Intent == Intent.Talk && !result.StartsWith("menu:"))
        {
            lines.Add("You cannot hold a conversation with that. Try 'examine' instead.");
            return;
        }

        if (parsed.Intent == Intent.Take)
        {
            if (result.StartsWith("menu:") || result.StartsWith("scene:") || result.StartsWith("timed:") || result.StartsWith("script:"))
            {
                lines.Add("You cannot take that.");
                return;
            }

            var takeFlag = $"taken:{_state.SceneId}:{resolvedAction}";
            if (_state.Flags.Contains(takeFlag))
            {
                lines.Add("You already took what you could there.");
                return;
            }
            _state.Flags.Add(takeFlag);
        }

        if (result.StartsWith("menu:"))
        {
            var menuId = result[5..];
            _state.ActiveMenuId = menuId;
            AppendMenuWithContext(menuId, lines, resolvedAction);
            return;
        }

        if (result.StartsWith("timed:"))
        {
            _state.ActiveTimedId = result[6..];
            lines.Add("The moment tightens.");
            return;
        }

        if (result.StartsWith("scene:"))
        {
            MoveToScene(result[6..], lines);
            AdvanceTime(lines, 1);
            return;
        }

        if (result.StartsWith("script:"))
        {
            RunScript(result[7..], lines);
            return;
        }

        lines.Add(result);
        AdvanceTime(lines, 1);
    }

    private void ResolveChanceEvent(string eventId, List<string> lines)
    {
        switch (eventId)
        {
            case "church_alms_box":
            {
                if (_state.Flags.Contains("event:church_alms_box_done"))
                {
                    lines.Add("You have already accounted for the alms box this week; the clerk waves you onward.");
                    return;
                }

                var score = _rng.Next(1, 21) + Virtue("charity") + Virtue("humility");
                if (score >= 18)
                {
                    lines.Add("You reconcile the alms ledger against offerings and uncover skimmed coin before scandal can spread.");
                    _state.Coin += 4;
                    _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") + 3, 0, 100);
                    AddVirtue("charity", 1);
                    lines.Add("Success: +4 coin, relations improved, and your charity and steadiness sharpen.");
                }
                else
                {
                    lines.Add("You audit the alms box, but your read is inconclusive; the matter remains unsettled.");
                    _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") - 1, 0, 100);
                    lines.Add("Outcome: no coin gained, minor local frustration.");
                }

                _state.Flags.Add("event:church_alms_box_done");
                return;
            }
            case "watch_patrol_scout":
            {
                var score = _rng.Next(1, 21) + Virtue("fortitude") + Virtue("hope");
                if (score >= 20)
                {
                    lines.Add("You read the treeline before dusk and spot movement early enough to warn the road wardens.");
                    _state.Priory["security"] = Math.Clamp(_state.Priory.GetValueOrDefault("security") + 2, 0, 100);
                    _state.Counters["watch_scout_success"] = _state.Counters.GetValueOrDefault("watch_scout_success") + 1;
                    lines.Add("Success: priory security improves.");
                }
                else
                {
                    lines.Add("You patrol hard but find only old sign and wind-bent brush.");
                    _state.Counters["watch_scout_attempt"] = _state.Counters.GetValueOrDefault("watch_scout_attempt") + 1;
                    lines.Add("No decisive result this time. You can try again later.");
                }
                return;
            }
        }

        lines.Add("Nothing comes of that attempt.");
    }

    private TimedPrompt? MaybeActivateTimed(List<string> lines)
    {
        if (_state.ActiveTimedId is null) return null;
        if (!_story.Timed.TryGetValue(_state.ActiveTimedId, out var timed)) return null;

        _activeTimed = timed;
        _state.ActiveTimedDeadline = DateTimeOffset.UtcNow.AddSeconds(timed.Seconds);
        lines.Add(timed.Prompt);

        _activeTimedOptionIndexes = timed.Options
            .Select((o, i) => (o, i))
            .Where(x => IsOptionAvailable(x.o, out _))
            .Select(x => x.i)
            .ToList();

        for (var i = 0; i < _activeTimedOptionIndexes.Count; i++)
        {
            var opt = timed.Options[_activeTimedOptionIndexes[i]];
            lines.Add($"{i + 1}) {opt.Text}");
        }

        if (_activeTimedOptionIndexes.Count == 0)
            lines.Add("(No options available; default will apply.)");

        return new TimedPrompt(timed.Id, timed.Prompt, _state.ActiveTimedDeadline.Value, timed.Seconds,
            _activeTimedOptionIndexes.Select(i => timed.Options[i].Text).ToArray());
    }

    private int ChooseDefaultTimedIndex(TimedDef timed)
    {
        var available = timed.Options
            .Select((o, i) => (o, i))
            .Where(x => IsOptionAvailable(x.o, out _))
            .ToList();
        if (available.Count == 0) return timed.DefaultIndex;

        var fort = Virtue("fortitude");
        var hum = Virtue("humility");
        var tem = Virtue("temperance");
        var cha = Virtue("charity");
        var faith = Virtue("faith");
        var hope = Virtue("hope");

        var best = available[Math.Clamp(timed.DefaultIndex, 0, available.Count - 1)].i;
        var bestScore = int.MinValue;
        foreach (var (opt, idx) in available)
        {
            var t = opt.Text.ToLowerInvariant();
            var score = 0;
            if (t.Contains("watch") || t.Contains("scan")) score += 2 * hope;
            if (t.Contains("warn") || t.Contains("call")) score += 2 * cha;
            if (t.Contains("seize") || t.Contains("grab") || t.Contains("ready")) score += 2 * fort;
            if (t.Contains("jump") || t.Contains("throw") || t.Contains("kick")) score += fort - tem;
            if (t.Contains("pray") || t.Contains("steady")) score += faith + hope;
            if (t.Contains("yield") || t.Contains("admit")) score += hum;
            if (score > bestScore)
            {
                bestScore = score;
                best = idx;
            }
        }

        return best;
    }

    private void AdvanceTime(List<string> lines, int segments)
    {
        for (var i = 0; i < segments; i++)
        {
            var idx = Array.IndexOf(SegmentOrder, _state.Segment);
            idx = (idx + 1) % SegmentOrder.Length;
            _state.Counters["segments_elapsed_today"] = _state.Counters.GetValueOrDefault("segments_elapsed_today") + 1;
            if (idx == 0)
            {
                _state.Day += 1;
                _state.Counters["segments_elapsed_today"] = 0;
                _state.Priory["food"] = Math.Clamp(_state.Priory["food"] - 1, 0, 100);
                _state.Priory["morale"] = Math.Clamp(_state.Priory["morale"] - (_state.Day % 5 == 0 ? 1 : 0), 0, 100);
                ProcessRebuildDay(lines);
            }
            _state.Segment = SegmentOrder[idx];
        }

        if (_state.Day % 7 == 0 && !_state.Flags.Contains($"week_{_state.Day}"))
        {
            _state.Flags.Add($"week_{_state.Day}");
            lines.Add("A week passes in Blackpine. News, grievances, and hopes gather at Saint Catherine.");

            var fort = Virtue("fortitude");
            var hum = Virtue("humility");
            var tem = Virtue("temperance");
            var cha = Virtue("charity");
            var fai = Virtue("faith");
            var hop = Virtue("hope");

            if (cha >= 4 && tem <= 0)
            {
                _state.Priory["treasury"] = Math.Clamp(_state.Priory.GetValueOrDefault("treasury") - 1, 0, 100);
                lines.Add("Your generosity outpaced reserves this week. Treasury -1 (Charity vs Temperance).");
            }

            if (fort >= 4 && hum <= 0)
            {
                _state.Priory["relations"] = Math.Clamp(_state.Priory.GetValueOrDefault("relations") - 1, 0, 100);
                lines.Add("Your firmness was respected by some and resented by others. Relations -1 (Fortitude vs Humility).");
            }

            if (hop >= 3)
            {
                _state.Priory["morale"] = Math.Clamp(_state.Priory.GetValueOrDefault("morale") + 1, 0, 100);
                lines.Add("Hope keeps the house from despair. Morale +1.");
            }

            if (fai >= 3)
            {
                _state.Priory["piety"] = Math.Clamp(_state.Priory.GetValueOrDefault("piety") + 1, 0, 100);
                lines.Add("Shared prayer steadies the priory in uncertainty. Piety +1.");
            }
        }
    }

    private void AttachSharedState()
    {
        if (_partyState is null)
        {
            _state.PartyId = null;
            return;
        }

        _state.PartyId = _partyState.PartyId;
        _state.Priory = _partyState.Priory;
        _state.Counters = _partyState.Counters;
        _state.Flags = _partyState.Flags;
        _state.ActiveQuests = _partyState.ActiveQuests;
        _state.CompletedQuests = _partyState.CompletedQuests;
    }

    private void SyncPartyState()
    {
        if (_partyState is null || string.IsNullOrWhiteSpace(_state.PartyId)) return;
        if (_partyRepo.TryLoadById(_state.PartyId, out var latest) && latest is not null)
        {
            _partyState = latest;
            AttachSharedState();
            RegisterPartyMember();
        }
    }

    private void PersistParty()
    {
        if (_partyState is null) return;
        RegisterPartyMember();
        _partyRepo.Save(_partyState);
    }

    private void RegisterPartyMember()
    {
        if (_partyState is null) return;
        if (_partyState.Members.Count >= 6 && !_partyState.Members.ContainsKey(_state.PlayerName)) return;

        _partyState.Members[_state.PlayerName] = new PartyMemberProfile
        {
            Name = _state.PlayerName,
            LastSceneId = _state.SceneId,
            LastSeenUtc = DateTimeOffset.UtcNow
        };
    }

    private void SetWorldFlag(string flag)
    {
        if (_state.Flags.Add(flag))
            RecordLoreEvent($"{_state.PlayerName} changed the course of Blackpine: {LoreFriendlyFlag(flag)}");
    }

    private void RecordLoreEvent(string summary)
    {
        if (_partyState is null) return;
        _partyState.LoreEvents.Add(new LoreEvent
        {
            ActorName = _state.PlayerName,
            Summary = summary,
            OccurredAtUtc = DateTimeOffset.UtcNow
        });

        if (_partyState.LoreEvents.Count > 100)
            _partyState.LoreEvents = _partyState.LoreEvents.TakeLast(100).ToList();
    }

    private void AppendPartyLoreRumors(List<string> lines)
    {
        if (_partyState is null) return;
        var unseen = _partyState.LoreEvents
            .Where(e => !e.ActorName.Equals(_state.PlayerName, StringComparison.OrdinalIgnoreCase))
            .Where(e => !_state.SeenLoreEventIds.Contains(e.Id))
            .Take(2)
            .ToList();

        foreach (var lore in unseen)
        {
            lines.Add($"A local quietly mentions that {lore.Summary}");
            _state.SeenLoreEventIds.Add(lore.Id);
        }
    }

    private string LoreFriendlyFlag(string flag)
        => flag.Replace("_", " ");

    private string PartyStatusLine()
    {
        if (_partyState is null) return "You travel alone. Use multiplayer setup on launch to create or join a party.";
        var members = _partyState.Members.Keys.OrderBy(x => x).ToArray();
        return $"Party {(_codec.MakePartyCode(_partyState.PartyId))} | Members ({members.Length}/6): {string.Join(", ", members)}";
    }

    private string TimeLine() => $"Day {_state.Day}, {_state.Segment}";
    private string InventoryLine()
    {
        var sterling = FormatSterling(_state.Coin);
        var marks = _state.Counters.GetValueOrDefault("hanse_mark_lubec");
        var purse = marks > 0
            ? $"{sterling} and {marks} Lübeck mark(s)"
            : sterling;

        return _state.Inventory.Count == 0
            ? $"{_state.PlayerName} | Purse: {purse}. Inventory: (empty)"
            : $"{_state.PlayerName} | Purse: {purse}. Inventory: {string.Join(", ", _state.Inventory)}";
    }


    private void ExchangePenceToMark(List<string> lines)
    {
        const int sterlingPencePerMark = 160;
        if (_state.Coin < sterlingPencePerMark)
        {
            lines.Add("The factor shakes his head: one Lübeck mark requires 160 sterling pennies.");
            return;
        }

        _state.Coin -= sterlingPencePerMark;
        _state.Counters["hanse_mark_lubec"] = _state.Counters.GetValueOrDefault("hanse_mark_lubec") + 1;
        lines.Add("You exchange 160d for 1 Lübeck mark at Ravenscar's Hanse table.");
    }

    private void ExchangeMarkToPence(List<string> lines)
    {
        const int sterlingPencePerMark = 150;
        var marks = _state.Counters.GetValueOrDefault("hanse_mark_lubec");
        if (marks <= 0)
        {
            lines.Add("You carry no Lübeck marks to redeem.");
            return;
        }

        _state.Counters["hanse_mark_lubec"] = marks - 1;
        _state.Coin += sterlingPencePerMark;
        lines.Add("You redeem 1 Lübeck mark for 150d after tolls, weighing fees, and broker's cut.");
    }

    private static string FormatSterling(int pennies)
    {
        var pounds = pennies / 240;
        var rem = pennies % 240;
        var shillings = rem / 12;
        var pence = rem % 12;
        return pounds > 0
            ? $"£{pounds} {shillings}s {pence}d ({pennies}d)"
            : $"{shillings}s {pence}d ({pennies}d)";
    }

    private string PrioryStatusLine() =>
        $"Priory - Food {_state.Priory["food"]}, Morale {_state.Priory["morale"]}, Piety {_state.Priory["piety"]}, Security {_state.Priory["security"]}, Relations {_state.Priory["relations"]}, Treasury {_state.Priory["treasury"]}";

    private string WorkTotalsLine() =>
        $"Work totals - Sermons {_state.Counters.GetValueOrDefault("task_sermon")}, Study {_state.Counters.GetValueOrDefault("task_study")}, Patrols {_state.Counters.GetValueOrDefault("task_patrol")}, Fields {_state.Counters.GetValueOrDefault("task_fields")}, Charity {_state.Counters.GetValueOrDefault("task_charity")}";

    private SceneDef CurrentScene() => _story.Scenes[_state.SceneId];

    private string ExitLine(SceneDef scene)
    {
        var exits = AvailableExits(scene);
        return exits.Count == 0
            ? "Travel options: none from here."
            : "From here you can travel to: " + string.Join(", ", exits.Keys);
    }

    private Dictionary<string, string> AvailableExits(SceneDef scene)
    {
        var exits = new Dictionary<string, string>(scene.Exits, StringComparer.OrdinalIgnoreCase);
        if (exits.ContainsKey("outside"))
            exits.Remove("outside");
        return exits;
    }

    private Dictionary<string, string> AvailableActions(SceneDef scene)
    {
        var actions = new Dictionary<string, string>(scene.Actions, StringComparer.OrdinalIgnoreCase);
        if (_state.Flags.Contains("event:cart_departed"))
        {
            const string cartDepartTurnCounter = "cart_departed_turn";
            var currentTurn = _state.Counters.GetValueOrDefault("turn_count");
            if (!_state.Counters.ContainsKey(cartDepartTurnCounter))
                _state.Counters[cartDepartTurnCounter] = currentTurn;

            var turnsSinceDeparture = currentTurn - _state.Counters.GetValueOrDefault(cartDepartTurnCounter);
            if (turnsSinceDeparture >= 3)
            {
                var unavailable = actions
                    .Where(kv => string.Equals(kv.Value, "timed:catch_cart", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in unavailable)
                    actions.Remove(key);
            }
        }
        return actions;
    }

    private bool IsOutsideRequest(ParsedInput parsed, string? target)
    {
        if (parsed.Verb is "exit") return true;
        if (string.IsNullOrWhiteSpace(target)) return false;

        var normalized = NormalizePhrase(target);
        return normalized is "outside" or "out" or "exit" or "street";
    }

    private bool TryMoveOutside(List<string> lines)
    {
        var scene = CurrentScene();
        if (scene.Exits.TryGetValue("outside", out var outsideScene))
        {
            MoveToScene(outsideScene, lines);
            return true;
        }

        var previousSceneId = _state.PreviousSceneId;
        if (string.IsNullOrWhiteSpace(previousSceneId) || !_story.Scenes.TryGetValue(previousSceneId, out var previousScene))
            return false;

        var linked = previousScene.Exits.Values.Contains(scene.Id, StringComparer.OrdinalIgnoreCase)
                     || scene.Exits.Values.Contains(previousScene.Id, StringComparer.OrdinalIgnoreCase);
        if (!linked) return false;

        MoveToScene(previousScene.Id, lines);
        return true;
    }

    private void MoveToScene(string nextSceneId, List<string> lines)
    {
        var previousSceneId = _state.SceneId;
        _state.SceneId = nextSceneId;
        _state.PreviousSceneId = previousSceneId;

        lines.Add(CurrentScene().Text);

        if (CurrentScene().EnterMenu is { } menu)
        {
            _state.ActiveMenuId = menu;
            AppendMenuWithContext(menu, lines);
        }
        else
        {
            lines.Add(ExitLine(CurrentScene()));
            AddContextTip(lines);
        }

        if (CurrentScene().EnterTimed is { } timed)
            _state.ActiveTimedId = timed;
    }

    private static bool TryResolveKey(IEnumerable<string> keys, string target, out string? resolved)
    {
        resolved = null;
        var keyList = keys.ToList();
        if (keyList.Count == 0) return false;

        var normalizedTarget = NormalizePhrase(target);
        if (keyList.FirstOrDefault(k => NormalizePhrase(k) == normalizedTarget) is { } exact)
        {
            resolved = exact;
            return true;
        }

        var targetTokens = ExpandTokens(Tokenize(normalizedTarget));
        var best = keyList
            .Select(k => (key: k, score: MatchScore(targetTokens, ExpandTokens(Tokenize(NormalizePhrase(k))))))
            .OrderByDescending(x => x.score)
            .First();

        if (best.score <= 0) return false;

        resolved = best.key;
        return true;
    }

    private static int MatchScore(HashSet<string> target, HashSet<string> candidate)
    {
        var overlap = target.Intersect(candidate).Count();
        if (overlap == 0) return 0;
        return overlap * 10 - Math.Abs(candidate.Count - target.Count);
    }

    private static HashSet<string> ExpandTokens(IEnumerable<string> tokens)
    {
        var set = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        foreach (var token in set.ToList())
            if (WordAliases.TryGetValue(token, out var aliases))
                foreach (var alias in aliases)
                    set.Add(alias);
        return set;
    }

    private static IEnumerable<string> Tokenize(string phrase)
        => phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x is not "to" and not "at" and not "the" and not "a" and not "an" and not "with" and not "into" and not "toward" and not "towards");

    private static string NormalizePhrase(string phrase)
        => string.Join(' ', phrase.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private string SaveNow()
    {
        PersistParty();
        var saveId = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        var path = Path.Combine(_saveRoot, saveId + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));

        var code = _codec.MakeCode(saveId);
        var fingerprint = _codec.StateFingerprint(JsonSerializer.Serialize(_state));
        return $"SAVE CODE: {code} | FP: {fingerprint}";
    }
}

public sealed record PartyMemberOverview(string Name, string LastSceneId, DateTimeOffset LastSeenUtc, int SecondsSinceSeen);
public sealed record PartyOverview(string PartyCode, List<PartyMemberOverview> Members);
public sealed record PlayerOverview(string PlayerName, PlayerSex Sex, string? LifePath, List<string> Inventory, int Coin);
