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

    public void StartNewGame(string playerName)
    {
        _state = new GameState
        {
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Pilgrim" : playerName.Trim()
        };
        AttachSharedState();
        IsEnded = false;
        _state.SceneId = "intro";
        _state.ActiveMenuId = "life_path";
        RegisterPartyMember();
        PersistParty();
        Console.WriteLine($"Welcome, {_state.PlayerName}.");
        if (IsPartyMode)
            Console.WriteLine($"Party bound to Saint Rose with code: {ActivePartyCode}");
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
    {
        if (!_partyRepo.TryLoadByCode(partyCode, out var party, out message) || party is null)
            return false;

        _partyState = party;
        ActivePartyCode = partyCode.Trim();
        return true;
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
                lines.Add("Common actions: look, go <place>, talk/speak <person>, examine <thing>, take <item>, inventory, status, quests, party, save, quit.");
                lines.Add("Tip: number choices (1, 2, 3...) select menu/timed options when they are shown.");
                break;
            case Intent.Look:
                lines.Add(CurrentScene().Text);
                lines.Add(ExitLine(CurrentScene()));
                AppendPartyLoreRumors(lines);
                lines.Add(TimeLine());
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
            case Intent.Quests:
                lines.Add(QuestLog());
                break;
            case Intent.Quit:
                IsEnded = true;
                lines.Add("Leaving game.");
                break;
            case Intent.Go:
                HandleMovement(parsed.Target, lines);
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
                HandleAction(parsed.Target, lines);
                break;
            default:
                lines.Add("I did not understand that. Try a verb + target (example: 'look', 'go gate', 'talk father') or type 'help'.");
                break;
        }

        PersistParty();
        var timedPrompt = MaybeActivateTimed(lines);
        return new(lines, timedPrompt);
    }

    private static IEnumerable<string> GameplayPrimer()
    {
        yield return "How to play: this world uses a verb processor. Enter a verb first, then what you want to act on.";
        yield return "Examples: 'look', 'go gate', 'talk steward', 'examine ledger', 'take candles'.";
        yield return "Use number choices when a menu appears. Type 'help' anytime for a quick command refresher.";
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
        if (!IsOptionAvailable(option, out var why))
        {
            lines.Add($"That path is unavailable: {why}");
            index = ChooseDefaultTimedIndex(_activeTimed);
            option = _activeTimed.Options[index];
        }

        ApplyOption(option, lines);
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
            return new(new List<string> { "Choose a number.", RenderMenu(menu) });

        var available = menu.Options
            .Select((o, i) => (o, i))
            .Where(x => IsOptionAvailable(x.o, out _))
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
        ApplyOption(option, lines);

        if (!(option.Script?.StartsWith("task:") ?? false))
            AdvanceTime(lines, 1);

        return new(lines, MaybeActivateTimed(lines));
    }

    private void ApplyLifePath(int index, List<string> lines)
    {
        var key = _story.LifePaths.Keys.OrderBy(x => x).ElementAt(index);
        var lp = _story.LifePaths[key];

        _state.ActiveMenuId = null;
        _state.LifePath = lp.Name;

        foreach (var kv in lp.VirtueDelta)
            _state.Virtues[kv.Key] = _state.Virtues.GetValueOrDefault(kv.Key) + kv.Value;

        _state.Coin = _rng.Next(lp.CoinMin, lp.CoinMax + 1);
        foreach (var item in lp.StarterItems)
            if (!_state.Inventory.Contains(item)) _state.Inventory.Add(item);

        lines.Add($"You have chosen: {lp.Name}");
        lines.Add($"Starting coin: {_state.Coin} silver pennies.");
        lines.Add("Starting items: " + string.Join(", ", lp.StarterItems));

        _state.SceneId = "house";
        lines.Add(CurrentScene().Text);
        lines.Add(ExitLine(CurrentScene()));
        AppendPartyLoreRumors(lines);

        if (!string.IsNullOrWhiteSpace(lp.IntroMenu))
        {
            _state.ActiveMenuId = lp.IntroMenu;
            lines.Add(RenderMenu(_story.Menus[lp.IntroMenu]));
        }

        StartQuest("main_rebuild_priory", lines);
    }

    private bool IsOptionAvailable(MenuOptionDef option, out string reason)
    {
        reason = "";
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

    private void ApplyOption(MenuOptionDef option, List<string> lines)
    {
        if (!string.IsNullOrWhiteSpace(option.Response)) lines.Add(option.Response);

        if (option.VirtueDelta is not null)
            foreach (var delta in option.VirtueDelta)
                _state.Virtues[delta.Key] = _state.Virtues.GetValueOrDefault(delta.Key) + delta.Value;

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

        if (!string.IsNullOrWhiteSpace(option.Script))
            RunScript(option.Script, lines);

        if (!string.IsNullOrWhiteSpace(option.NextScene))
        {
            _state.SceneId = option.NextScene;
            lines.Add(CurrentScene().Text);
            lines.Add(ExitLine(CurrentScene()));
            if (CurrentScene().EndChapter) IsEnded = true;
        }

        if (!string.IsNullOrWhiteSpace(option.NextMenu))
        {
            _state.ActiveMenuId = option.NextMenu;
            lines.Add(RenderMenu(_story.Menus[option.NextMenu]));
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

        switch (script)
        {
            case "check_progress":
                CheckProgress(lines);
                return;
            case "goto_village_arc":
                GateArc("arc_village", "village_crisis", "Blackpine has not yet brought this dispute formally to Saint Rose. Continue your ordinary labors.", lines);
                return;
            case "goto_york_arc":
                GateArc("arc_york", "york_letters", "No summons from York has yet arrived.", lines);
                return;
            case "goto_winter_arc":
                GateArc("arc_longwinter", "long_winter", "Winter has not yet forced the priory into emergency measures.", lines);
                return;
            case "goto_avignon_arc":
                GateArc("arc_avignon", "avignon_chapterhouse", "No Avignon-linked patronal packet has yet reached Saint Rose.", lines);
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
                GateArc("arc_border", "border_refuge", "No border deputation has yet asked Saint Rose for aid.", lines);
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
                lines.Add("A sealed Hanse packet pulls Saint Rose into coastal contracts and scrutiny.");
                return;
            case "start_germany_arc":
                SetWorldFlag("arc_reich_letters");
                StartQuest("reich_theology_letters", lines);
                lines.Add("Letters from the Rhineland studia raise grave theological and pastoral questions.");
                return;
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
            lines.Add("A hard winter sets in. Supplies tighten, rumors multiply, and Saint Rose must decide what to protect first.");
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
            lines.Add("Wool and candle prices convulse; guild delegates now court Saint Rose with polished promises.");
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
            lines.Add("An internal breach at Saint Rose forces discipline, truth, and mercy into painful collision.");
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
            lines.Add("Saint Rose is renewed: chapel, school, infirmary, and alms-house all endure. Your governance joined truth with mercy.");
        else if (score >= 290)
            lines.Add("Saint Rose survives with scars. Some holdings are lost, but the priory remains a living witness in Blackpine.");
        else
            lines.Add("Saint Rose is diminished under debt and pressure. Yet fragments of doctrine, charity, and memory remain for another generation.");

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
                _state.Virtues["prudence"] += 1;
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
                _state.Virtues["fortitude"] += 1;
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
            var yourRoll = _rng.Next(1, 7) + _rng.Next(1, 7) + (_state.Virtues["temperance"] > 2 ? 1 : 0);
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
            _state.Virtues["temperance"] = Math.Max(_state.Virtues["temperance"] - 1, -10);
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
            var recollection = _rng.Next(1, 7) + Math.Max(0, _state.Virtues["prudence"]) + Math.Max(0, _state.Virtues["temperance"]);
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
        _state.Virtues["temperance"] += 1;
        if (focus >= 4) _state.Virtues["prudence"] += 1;

        lines.Add(focus >= 4
            ? "Prayer steadies your judgment for the day."
            : "Prayer gives enough peace to continue faithfully.");

        AdvanceTime(lines, 1);
    }

    private void GoFishing(List<string> lines)
    {
        var attempts = 3 + Math.Max(0, _state.Virtues["fortitude"]/3);
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
            _state.Virtues["temperance"] += 1;
        }

        AdvanceTime(lines, 1);
    }

    private string Pick(params string[] lines) => lines[_rng.Next(lines.Length)];

    private string RenderMenu(MenuDef menu)
    {
        var sb = new StringBuilder();
        sb.AppendLine(menu.Prompt);
        var available = menu.Options.Where(o => IsOptionAvailable(o, out _)).ToList();
        for (var i = 0; i < available.Count; i++)
            sb.AppendLine($"{i + 1}) {available[i].Text}");
        if (available.Count == 0)
            sb.AppendLine("(No options currently available.)");
        return sb.ToString();
    }

    private void HandleMovement(string? target, List<string> lines)
    {
        var scene = CurrentScene();
        if (string.IsNullOrWhiteSpace(target))
        {
            lines.Add("Go where?");
            return;
        }

        if (!scene.Exits.TryGetValue(target, out var next))
        {
            lines.Add("You cannot go that way.");
            return;
        }

        _state.SceneId = next;
        lines.Add(CurrentScene().Text);
        lines.Add(ExitLine(CurrentScene()));

        if (CurrentScene().EnterMenu is { } menu)
        {
            _state.ActiveMenuId = menu;
            lines.Add(RenderMenu(_story.Menus[menu]));
        }

        if (CurrentScene().EnterTimed is { } timed)
            _state.ActiveTimedId = timed;

        AdvanceTime(lines, 1);
        if (CurrentScene().EndChapter) IsEnded = true;
    }

    private void HandleAction(string? target, List<string> lines)
    {
        var scene = CurrentScene();
        if (string.IsNullOrWhiteSpace(target))
        {
            lines.Add("Be specific.");
            return;
        }

        if (!scene.Actions.TryGetValue(target, out var result))
        {
            lines.Add("Nothing comes of it.");
            return;
        }

        if (result.StartsWith("menu:"))
        {
            var menuId = result[5..];
            _state.ActiveMenuId = menuId;
            lines.Add(RenderMenu(_story.Menus[menuId]));
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
            _state.SceneId = result[6..];
            lines.Add(CurrentScene().Text);
            lines.Add(ExitLine(CurrentScene()));
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

        var fort = _state.Virtues.GetValueOrDefault("fortitude");
        var pru = _state.Virtues.GetValueOrDefault("prudence");
        var tem = _state.Virtues.GetValueOrDefault("temperance");
        var jus = _state.Virtues.GetValueOrDefault("justice");

        var best = available[Math.Clamp(timed.DefaultIndex, 0, available.Count - 1)].i;
        var bestScore = int.MinValue;
        foreach (var (opt, idx) in available)
        {
            var t = opt.Text.ToLowerInvariant();
            var score = 0;
            if (t.Contains("watch") || t.Contains("scan")) score += 2 * pru;
            if (t.Contains("warn") || t.Contains("call")) score += 2 * jus;
            if (t.Contains("seize") || t.Contains("grab") || t.Contains("ready")) score += 2 * fort;
            if (t.Contains("jump") || t.Contains("throw") || t.Contains("kick")) score += fort - tem;
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
            if (idx == 0)
            {
                _state.Day += 1;
                _state.Priory["food"] = Math.Clamp(_state.Priory["food"] - 1, 0, 100);
                _state.Priory["morale"] = Math.Clamp(_state.Priory["morale"] - (_state.Day % 5 == 0 ? 1 : 0), 0, 100);
            }
            _state.Segment = SegmentOrder[idx];
        }

        if (_state.Day % 7 == 0 && !_state.Flags.Contains($"week_{_state.Day}"))
        {
            _state.Flags.Add($"week_{_state.Day}");
            lines.Add("A week passes in Blackpine. News, grievances, and hopes gather at Saint Rose.");
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
        return $"Party active ({_partyState.PartyId}) with {members.Length} companion(s): {string.Join(", ", members)}";
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

    private static string ExitLine(SceneDef scene)
        => scene.Exits.Count == 0 ? "Exits: none" : "Exits: " + string.Join(", ", scene.Exits.Keys);

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
