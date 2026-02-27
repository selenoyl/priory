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
            {
                Console.WriteLine(RenderMenu(menu));
            }
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
        {
            return ResolveMenu(parsed, menuId);
        }

        if (_activeTimed is not null)
        {
            lines.Add("Timed prompt active. Enter a number.");
            return new(lines);
        }

        switch (parsed.Intent)
        {
            case Intent.Help:
                lines.Add("Commands: look, go <place>, talk/speak <person>, examine <thing>, inventory, status, save, quit.");
                break;
            case Intent.Look:
                lines.Add(CurrentScene().Text);
                lines.Add(ExitLine(CurrentScene()));
                lines.Add(TimeLine());
                break;
            case Intent.Inventory:
                lines.Add($"Coin: {_state.Coin} silver pennies.");
                lines.Add(_state.Inventory.Count == 0
                    ? "Inventory: (empty)"
                    : "Inventory: " + string.Join(", ", _state.Inventory));
                lines.Add($"Virtues: Pru {_state.Virtues["prudence"]}, For {_state.Virtues["fortitude"]}, Tem {_state.Virtues["temperance"]}, Jus {_state.Virtues["justice"]}");
                break;
            case Intent.Status:
                lines.Add(TimeLine());
                lines.Add($"Priory - Food {_state.Priory["food"]}, Morale {_state.Priory["morale"]}, Piety {_state.Priory["piety"]}, Security {_state.Priory["security"]}, Relations {_state.Priory["relations"]}, Treasury {_state.Priory["treasury"]}");
                lines.Add($"Work totals - Sermons {_state.Counters.GetValueOrDefault("task_sermon")}, Study {_state.Counters.GetValueOrDefault("task_study")}, Patrols {_state.Counters.GetValueOrDefault("task_patrol")}, Fields {_state.Counters.GetValueOrDefault("task_fields")}, Charity {_state.Counters.GetValueOrDefault("task_charity")}");
                break;
            case Intent.Save:
                lines.Add(SaveNow());
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
        {
            lines.Add("No timed event is active.");
            return new(lines);
        }

        var index = selected - 1;
        if (index < 0 || index >= _activeTimed.Options.Count)
        {
            index = ChooseDefaultTimedIndex(_activeTimed);
            lines.Add("You hesitate. The moment chooses for you.");
        }

        var option = _activeTimed.Options[index];
        ApplyOption(option, lines);
        _activeTimed = null;
        _state.ActiveTimedId = null;
        _state.ActiveTimedDeadline = null;

        AdvanceTime(lines, 1);
        var timedPrompt = MaybeActivateTimed(lines);
        return new(lines, timedPrompt);
    }

    private EngineOutput ResolveMenu(ParsedInput parsed, string menuId)
    {
        var lines = new List<string>();
        if (!_story.Menus.TryGetValue(menuId, out var menu))
        {
            _state.ActiveMenuId = null;
            lines.Add("Menu missing; continuing.");
            return new(lines);
        }

        if (parsed.Intent == Intent.Save)
        {
            lines.Add(SaveNow());
            lines.Add(RenderMenu(menu));
            return new(lines);
        }

        if (parsed.Intent != Intent.Numeric)
        {
            lines.Add("Choose a number.");
            lines.Add(RenderMenu(menu));
            return new(lines);
        }

        var index = parsed.Number - 1;
        if (index < 0 || index >= menu.Options.Count)
        {
            lines.Add("That option is not available.");
            lines.Add(RenderMenu(menu));
            return new(lines);
        }

        if (menu.Id == "life_path")
        {
            ApplyLifePath(index, lines);
            var timedPrompt = MaybeActivateTimed(lines);
            return new(lines, timedPrompt);
        }

        var option = menu.Options[index];
        _state.ActiveMenuId = null;
        ApplyOption(option, lines);
        if (!(option.Script?.StartsWith("task:") ?? false))
            AdvanceTime(lines, 1);

        var nextTimed = MaybeActivateTimed(lines);
        return new(lines, nextTimed);
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
            var type = script[5..];
            ExecuteTask(type, lines);
            return;
        }

        if (script == "check_progress")
        {
            var total = _state.Counters.Where(x => x.Key.StartsWith("task_")).Sum(x => x.Value);
            if (total >= 8 && !_state.Flags.Contains("arc_village"))
            {
                _state.Flags.Add("arc_village");
                lines.Add("Word spreads through Blackpine: the priory's labors are changing village life. New disputes and petitions arrive.");
                return;
            }

            if (total >= 18 && !_state.Flags.Contains("arc_york"))
            {
                _state.Flags.Add("arc_york");
                lines.Add("A Dominican courier arrives from York with letters on doctrine, debt, and disorder. The stakes rise.");
                return;
            }

            if (total >= 28 && !_state.Flags.Contains("arc_longwinter"))
            {
                _state.Flags.Add("arc_longwinter");
                lines.Add("A hard winter sets in. Supplies tighten, rumors multiply, and Saint Rose must decide what to protect first.");
                return;
            }

            lines.Add("The chapter's long arc continues to gather quietly.");
            return;
        }


        if (script == "goto_village_arc")
        {
            if (_state.Flags.Contains("arc_village"))
            {
                _state.SceneId = "village_crisis";
                lines.Add(CurrentScene().Text);
                lines.Add(ExitLine(CurrentScene()));
            }
            else
            {
                lines.Add("Blackpine has not yet brought this dispute formally to Saint Rose. Continue your ordinary labors.");
            }
            return;
        }

        if (script == "goto_york_arc")
        {
            if (_state.Flags.Contains("arc_york"))
            {
                _state.SceneId = "york_letters";
                lines.Add(CurrentScene().Text);
                lines.Add(ExitLine(CurrentScene()));
            }
            else
            {
                lines.Add("No summons from York has yet arrived.");
            }
            return;
        }

        if (script == "goto_winter_arc")
        {
            if (_state.Flags.Contains("arc_longwinter"))
            {
                _state.SceneId = "long_winter";
                lines.Add(CurrentScene().Text);
                lines.Add(ExitLine(CurrentScene()));
            }
            else
            {
                lines.Add("Winter has not yet forced the priory into emergency measures.");
            }
            return;
        }
        if (script == "resolve_endgame")
        {
            var score = _state.Priory["food"] + _state.Priory["morale"] + _state.Priory["piety"] + _state.Priory["security"] + _state.Priory["relations"];
            if (score >= 320)
            {
                lines.Add("Saint Rose endures with its library, altar, and poorhouse intact. Your years of patient labor held the line.");
            }
            else if (score >= 250)
            {
                lines.Add("Saint Rose survives, though reduced. Some fields are sold, but the chapel lamp remains lit.");
            }
            else
            {
                lines.Add("Saint Rose is broken apart under pressure of debt, fear, and division. Yet fragments of its witness remain.");
            }
            IsEnded = true;
            return;
        }
    }

    private void ExecuteTask(string type, List<string> lines)
    {
        switch (type)
        {
            case "sermon":
                _state.Counters["task_sermon"] = _state.Counters.GetValueOrDefault("task_sermon") + 1;
                _state.Priory["piety"] = Math.Clamp(_state.Priory["piety"] + 2, 0, 100);
                _state.Priory["relations"] = Math.Clamp(_state.Priory["relations"] + 1, 0, 100);
                lines.Add(Pick(
                    "You preach at Blackpine's edge: mercy without softness, truth without cruelty.",
                    "In the market lane, you answer questions on confession, debt, and conscience until dusk.",
                    "At a roadside shrine, your short sermon steadies frightened travelers."));
                break;
            case "study":
                _state.Counters["task_study"] = _state.Counters.GetValueOrDefault("task_study") + 1;
                _state.Virtues["prudence"] += 1;
                _state.Priory["piety"] = Math.Clamp(_state.Priory["piety"] + 1, 0, 100);
                lines.Add(Pick(
                    "You copy a disputed tract beside a Dominican gloss and mark where argument outruns charity.",
                    "Brother Martin drills you in logic at the scriptorium table until your head aches and clarifies.",
                    "You prepare a lecture for younger brothers on justice in contracts and wages."));
                break;
            case "patrol":
                _state.Counters["task_patrol"] = _state.Counters.GetValueOrDefault("task_patrol") + 1;
                _state.Priory["security"] = Math.Clamp(_state.Priory["security"] + 2, 0, 100);
                _state.Virtues["fortitude"] += 1;
                lines.Add(Pick(
                    "You walk the forest road at vespers. The bandit signs are old, but not gone.",
                    "A tense standoff at the mill ends without blood when you hold your ground.",
                    "You escort a widow's cart through a dangerous bend and return long after compline."));
                break;
            case "fields":
                _state.Counters["task_fields"] = _state.Counters.GetValueOrDefault("task_fields") + 1;
                _state.Priory["food"] = Math.Clamp(_state.Priory["food"] + 2, 0, 100);
                _state.Priory["treasury"] = Math.Clamp(_state.Priory["treasury"] + 1, 0, 100);
                lines.Add(Pick(
                    "You reorganize winter stores and catch theft in the tally before it ruins the week.",
                    "At the barley strip, you settle a quarrel over boundaries and save the harvest line.",
                    "You spend a wet day mending ditches; by evening the lower field drains cleanly."));
                break;
            case "charity":
                _state.Counters["task_charity"] = _state.Counters.GetValueOrDefault("task_charity") + 1;
                _state.Priory["morale"] = Math.Clamp(_state.Priory["morale"] + 2, 0, 100);
                _state.Priory["relations"] = Math.Clamp(_state.Priory["relations"] + 2, 0, 100);
                _state.Priory["treasury"] = Math.Clamp(_state.Priory["treasury"] - 1, 0, 100);
                lines.Add(Pick(
                    "You distribute bread and lamp oil at dawn, and hear confessions in a freezing side aisle.",
                    "A sick child survives the week after the priory sends broth, wool, and nursing shifts.",
                    "You mediate a marriage debt and trade coin for peace neither side thought possible."));
                break;
        }

        AdvanceTime(lines, 1);
    }

    private string Pick(params string[] lines) => lines[_rng.Next(lines.Length)];

    private string RenderMenu(MenuDef menu)
    {
        var sb = new StringBuilder();
        sb.AppendLine(menu.Prompt);
        for (var i = 0; i < menu.Options.Count; i++)
            sb.AppendLine($"{i + 1}) {menu.Options[i].Text}");
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

        if (scene.Exits.TryGetValue(target, out var next))
        {
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
            return;
        }

        lines.Add("You cannot go that way.");
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
        for (var i = 0; i < timed.Options.Count; i++)
            lines.Add($"{i + 1}) {timed.Options[i].Text}");

        return new TimedPrompt(timed.Id, timed.Prompt, _state.ActiveTimedDeadline.Value, timed.Seconds,
            timed.Options.Select(o => o.Text).ToArray());
    }

    private int ChooseDefaultTimedIndex(TimedDef timed)
    {
        var fort = _state.Virtues.GetValueOrDefault("fortitude");
        var pru = _state.Virtues.GetValueOrDefault("prudence");
        var tem = _state.Virtues.GetValueOrDefault("temperance");
        var jus = _state.Virtues.GetValueOrDefault("justice");

        var best = timed.DefaultIndex;
        var bestScore = int.MinValue;
        for (var i = 0; i < timed.Options.Count; i++)
        {
            var t = timed.Options[i].Text.ToLowerInvariant();
            var score = 0;
            if (t.Contains("watch") || t.Contains("scan")) score += 2 * pru;
            if (t.Contains("warn") || t.Contains("call")) score += 2 * jus;
            if (t.Contains("seize") || t.Contains("grab") || t.Contains("ready")) score += 2 * fort;
            if (t.Contains("jump") || t.Contains("throw") || t.Contains("kick")) score += fort - tem;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return Math.Clamp(best, 0, timed.Options.Count - 1);
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
