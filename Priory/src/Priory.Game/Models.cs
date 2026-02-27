namespace Priory.Game;

public sealed class GameState
{
    public string SceneId { get; set; } = "intro";
    public string? LifePath { get; set; }
    public int Coin { get; set; }
    public Dictionary<string, int> Virtues { get; set; } = new()
    {
        ["prudence"] = 0,
        ["fortitude"] = 0,
        ["temperance"] = 0,
        ["justice"] = 0
    };
    public List<string> Inventory { get; set; } = new();
    public string? ActiveMenuId { get; set; }
    public string? ActiveTimedId { get; set; }
    public DateTimeOffset? ActiveTimedDeadline { get; set; }
}

public sealed class StoryData
{
    public Dictionary<string, SceneDef> Scenes { get; set; } = new();
    public Dictionary<string, MenuDef> Menus { get; set; } = new();
    public Dictionary<string, TimedDef> Timed { get; set; } = new();
    public Dictionary<string, LifePathDef> LifePaths { get; set; } = new();
}

public sealed class SceneDef
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public Dictionary<string, string> Exits { get; set; } = new();
    public Dictionary<string, string> Actions { get; set; } = new();
    public string? EnterMenu { get; set; }
    public string? EnterTimed { get; set; }
    public bool EndChapter { get; set; }
}

public sealed class MenuDef
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<MenuOptionDef> Options { get; set; } = new();
}

public sealed class MenuOptionDef
{
    public string Text { get; set; } = "";
    public string? NextScene { get; set; }
    public string? Response { get; set; }
    public Dictionary<string, int>? VirtueDelta { get; set; }
    public int CoinDelta { get; set; }
    public List<string>? AddItems { get; set; }
    public List<string>? RemoveItems { get; set; }
    public string? NextMenu { get; set; }
    public string? NextTimed { get; set; }
}

public sealed class TimedDef
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int Seconds { get; set; }
    public int DefaultIndex { get; set; }
    public List<MenuOptionDef> Options { get; set; } = new();
}

public sealed class LifePathDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, int> VirtueDelta { get; set; } = new();
    public int CoinMin { get; set; }
    public int CoinMax { get; set; }
    public List<string> StarterItems { get; set; } = new();
}

public sealed record TimedPrompt(string Id, string Prompt, DateTimeOffset Deadline, IReadOnlyList<string> Options);
public sealed record EngineOutput(List<string> Lines, TimedPrompt? TimedPrompt = null);
