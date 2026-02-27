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
    private readonly Random _rng = new();

    private GameState _state = new();
    private TimedDef? _activeTimed;

    public bool IsEnded { get; private set; }

    public GameEngine(StoryData story, string saveRoot, SaveCodec codec)
    {
        _story = story;
        _saveRoot = saveRoot;
        _codec = codec;
    }

    public void StartNewGame()
    {
        _state = new GameState();
        IsEnded = false;
        _state.SceneId = "intro";
        _state.ActiveMenuId = "life_path";
        Console.WriteLine(CurrentScene().Text);
        Console.WriteLine(RenderMenu(_story.Menus["life_path"]));
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
            IsEnded = false;
            message = "Resumed saved journey.";
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
        var lines = new List<string>();
        var parsed = Parser.Parse(raw);

        if (_state.ActiveMenuId is { } menuId)
            return ResolveMenu(parsed, menuId);

        if (_activeTimed is not null)
            return new(new List<string> { "Timed prompt active. Enter a number." });

        switch (parsed.Intent)
        {
            case Intent.Help:
                lines.Add("Commands: look, go <place>, talk/speak <thing>, examine <thing>, inventory, status, quests, save, quit.");
                break;
            case Intent.Look:
                lines.Add(CurrentScene().Text);
                lines.Add(ExitLine(CurrentScene()));
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
                HandleAction(parsed.Target, lines);
                break;
            default:
                lines.Add("I did not understand that.");
                break;
        }

        var timedPrompt = MaybeActivateTimed(lines);
        return new(lines, timedPrompt);
    }

    public EngineOutput ResolveTimed(int selected)
    {
        var lines = new List<string>();
        if (_activeTimed is null)
            return new(new List<string> { "No timed event is active." });

        var index = selected - 1;
        if (index < 0 || index >= _activeTimed.Options.Count)
        {
            index = ChooseDefaultTimedIndex(_activeTimed);
            lines.Add("You hesitate. The moment chooses for you.");
        }

        var option = _activeTimed.Options[index];
        if (!IsOptionAvailable(option, out var why))
        {
            lines.Add($"That path is unavailable: {why}");
            option = _activeTimed.Options[ChooseDefaultTimedIndex(_activeTimed)];
        }

        ApplyOption(option, lines);
        _activeTimed = null;
        _state.ActiveTimedId = null;
        _state.ActiveTimedDeadline = null;

        AdvanceTime(lines, 1);
        return new(lines, MaybeActivateTimed(lines));
    }

    private EngineOutput ResolveMenu(ParsedInput parsed, string menuId)
    {
        var lines = new List<string>();
        if (!_story.Menus.TryGetValue(menuId, out var menu))
        {
            _state.ActiveMenuId = null;
            return new(new List<string> { "Menu missing; continuing." });
        }

        if (parsed.Intent == Intent.Save)
            return new(new List<string> { SaveNow(), RenderMenu(menu) });

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
                _state.Flags.Add(flag);

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
            case "resolve_endgame":
                ResolveEndgame(lines);
                return;
            case "questlog":
                lines.Add(QuestLog());
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
            lines.Add($"  [{idx}] {item.Name} - {stock.Price} pennies ({item.Description})");
            idx++;
        }
        lines.Add("Type the shop action again using its purchase menu to buy specific goods.");
    }

    private void CheckProgress(List<string> lines)
    {
        var total = _state.Counters.GetValueOrDefault("task_total");

        if (total >= 8 && !_state.Flags.Contains("arc_village"))
        {
            _state.Flags.Add("arc_village");
            StartQuest("village_petitions", lines);
            lines.Add("Word spreads through Blackpine: the priory's labors are changing village life. New disputes and petitions arrive.");
            return;
        }

        if (total >= 16 && !_state.Flags.Contains("arc_orders"))
        {
            _state.Flags.Add("arc_orders");
            StartQuest("orders_concord", lines);
            lines.Add("Franciscans, Carmelite travelers, and local Benedictine agents each seek influence in Blackpine.");
            return;
        }

        if (total >= 24 && !_state.Flags.Contains("arc_york"))
        {
            _state.Flags.Add("arc_york");
            StartQuest("york_deputation", lines);
            lines.Add("A Dominican courier arrives from York with letters on doctrine, debt, and disorder. The stakes rise.");
            return;
        }

        if (total >= 32 && !_state.Flags.Contains("arc_longwinter"))
        {
            _state.Flags.Add("arc_longwinter");
            StartQuest("winter_mercy", lines);
            lines.Add("A hard winter sets in. Supplies tighten, rumors multiply, and Saint Rose must decide what to protect first.");
            return;
        }

        if (total >= 40 && !_state.Flags.Contains("arc_final"))
        {
            _state.Flags.Add("arc_final");
            lines.Add("The first great rebuilding cycle is complete. The priory now faces consequences of everything you have chosen.");
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
        _state.ActiveQuests.Add(questId);
        lines.Add($"[Quest Started] {q.Title}: {q.Description}");
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

        var available = timed.Options.Where(o => IsOptionAvailable(o, out _)).ToList();
        for (var i = 0; i < available.Count; i++)
            lines.Add($"{i + 1}) {available[i].Text}");

        if (available.Count == 0)
            lines.Add("(No options available; default will apply.)");

        return new TimedPrompt(timed.Id, timed.Prompt, _state.ActiveTimedDeadline.Value, timed.Seconds,
            available.Select(o => o.Text).ToArray());
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

    private string TimeLine() => $"Day {_state.Day}, {_state.Segment}";
    private string InventoryLine() => _state.Inventory.Count == 0
        ? $"Coin: {_state.Coin} silver pennies. Inventory: (empty)"
        : $"Coin: {_state.Coin} silver pennies. Inventory: {string.Join(", ", _state.Inventory)}";

    private string PrioryStatusLine() =>
        $"Priory - Food {_state.Priory["food"]}, Morale {_state.Priory["morale"]}, Piety {_state.Priory["piety"]}, Security {_state.Priory["security"]}, Relations {_state.Priory["relations"]}, Treasury {_state.Priory["treasury"]}";

    private string WorkTotalsLine() =>
        $"Work totals - Sermons {_state.Counters.GetValueOrDefault("task_sermon")}, Study {_state.Counters.GetValueOrDefault("task_study")}, Patrols {_state.Counters.GetValueOrDefault("task_patrol")}, Fields {_state.Counters.GetValueOrDefault("task_fields")}, Charity {_state.Counters.GetValueOrDefault("task_charity")}";

    private SceneDef CurrentScene() => _story.Scenes[_state.SceneId];

    private static string ExitLine(SceneDef scene)
        => scene.Exits.Count == 0 ? "Exits: none" : "Exits: " + string.Join(", ", scene.Exits.Keys);

    private string SaveNow()
    {
        var saveId = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        var path = Path.Combine(_saveRoot, saveId + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));

        var code = _codec.MakeCode(saveId);
        var fingerprint = _codec.StateFingerprint(JsonSerializer.Serialize(_state));
        return $"SAVE CODE: {code} | FP: {fingerprint}";
    }
}
