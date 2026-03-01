namespace Priory.Game;

public enum TimeSegment
{
    Matins,
    Prime,
    Sext,
    Vespers,
    Compline
}

public enum PlayerSex
{
    Unknown,
    Male,
    Female
}

public sealed class GameState
{
    public string PlayerName { get; set; } = "Pilgrim";
    public PlayerSex Sex { get; set; } = PlayerSex.Unknown;
    public string? PartyId { get; set; }
    public string SceneId { get; set; } = "intro";
    public string? PreviousSceneId { get; set; }
    public string? LifePath { get; set; }
    public int Coin { get; set; }
    public int Day { get; set; } = 1;
    public TimeSegment Segment { get; set; } = TimeSegment.Prime;
    public Dictionary<string, int> Virtues { get; set; } = new()
    {
        ["fortitude"] = 0,
        ["temperance"] = 0,
        ["faith"] = 0,
        ["hope"] = 0,
        ["charity"] = 0,
        ["humility"] = 0
    };
    public Dictionary<string, int> Priory { get; set; } = new()
    {
        ["food"] = 50,
        ["morale"] = 50,
        ["piety"] = 50,
        ["security"] = 50,
        ["relations"] = 50,
        ["treasury"] = 30
    };
    public Dictionary<string, int> Counters { get; set; } = new();
    public HashSet<string> Flags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Inventory { get; set; } = new();
    public HashSet<string> ActiveQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CompletedQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ActiveMenuId { get; set; }
    public string? ActiveTimedId { get; set; }
    public DateTimeOffset? ActiveTimedDeadline { get; set; }
    public HashSet<string> SeenLoreEventIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public RebuildState Rebuild { get; set; } = new();
}

public sealed class PartyState
{
    public string PartyId { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, int> Priory { get; set; } = new()
    {
        ["food"] = 50,
        ["morale"] = 50,
        ["piety"] = 50,
        ["security"] = 50,
        ["relations"] = 50,
        ["treasury"] = 30
    };
    public Dictionary<string, int> Counters { get; set; } = new();
    public HashSet<string> Flags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ActiveQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CompletedQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LoreEvent> LoreEvents { get; set; } = new();
    public Dictionary<string, PartyMemberProfile> Members { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LoreEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ActorName { get; set; } = "A companion";
    public string Summary { get; set; } = "A choice changed Blackpine.";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PartyMemberProfile
{
    public string Name { get; set; } = "Pilgrim";
    public string LastSceneId { get; set; } = "intro";
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StoryData
{
    public Dictionary<string, SceneDef> Scenes { get; set; } = new();
    public Dictionary<string, MenuDef> Menus { get; set; } = new();
    public Dictionary<string, TimedDef> Timed { get; set; } = new();
    public Dictionary<string, LifePathDef> LifePaths { get; set; } = new();
    public Dictionary<string, QuestDef> Quests { get; set; } = new();
    public Dictionary<string, ItemDef> Items { get; set; } = new();
    public Dictionary<string, ShopDef> Shops { get; set; } = new();
    public Dictionary<string, RebuildNodeDef> RebuildNodes { get; set; } = new();
}



public sealed class RebuildNodeDef
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<RebuildLevelDef> Levels { get; set; } = new();
    public Dictionary<string, int> MinStats { get; set; } = new();
    public Dictionary<string, int> RequiresNodeLevels { get; set; } = new();
}

public sealed class RebuildLevelDef
{
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public Dictionary<string, int> Cost { get; set; } = new();
    public int TimeDays { get; set; }
    public int LaborPerDay { get; set; } = 3;
    public Dictionary<string, int> StatDelta { get; set; } = new();
    public List<string> Unlocks { get; set; } = new();
}

public sealed class ActiveRebuildProject
{
    public string NodeId { get; set; } = "";
    public int TargetLevel { get; set; }
    public int DaysRemaining { get; set; }
    public int RequiredLaborPerDay { get; set; } = 3;
}

public sealed class RebuildState
{
    public Dictionary<string, int> NodeLevels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stability"] = 0,
        ["defense"] = 0,
        ["hospitality"] = 0,
        ["sanctity"] = 0,
        ["scholarship"] = 0,
        ["economy"] = 0
    };
    public Dictionary<string, int> LaborAssigned { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monks"] = 2,
        ["laybrothers"] = 1,
        ["workers"] = 0
    };
    public int LaborStress { get; set; }
    public int VisitorsToday { get; set; }
    public int VisitorCapacity { get; set; } = 4;
    public int DonationsTotal { get; set; }
    public ActiveRebuildProject? ActiveProject { get; set; }
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
    public Dictionary<string, int>? PrioryDelta { get; set; }
    public Dictionary<string, int>? CounterDelta { get; set; }
    public List<string>? SetFlags { get; set; }
    public List<string>? ClearFlags { get; set; }
    public List<string>? RequireFlags { get; set; }
    public List<string>? RequireNotFlags { get; set; }
    public List<string>? RequireSexes { get; set; }
    public List<string>? RequireNotSexes { get; set; }
    public int CoinDelta { get; set; }
    public List<string>? AddItems { get; set; }
    public List<string>? RemoveItems { get; set; }
    public string? NextMenu { get; set; }
    public string? NextTimed { get; set; }
    public string? Script { get; set; }
    public string? StartQuest { get; set; }
    public string? CompleteQuest { get; set; }
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
    public string? IntroMenu { get; set; }
}

public sealed class QuestDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Category { get; set; }
    public int MinPartySize { get; set; } = 1;
    public bool RequiresSynchronizedParty { get; set; }
}

public sealed class ItemDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Value { get; set; }
}

public sealed class ShopDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ShopStockDef> Stock { get; set; } = new();
}

public sealed class ShopStockDef
{
    public string ItemId { get; set; } = "";
    public int Price { get; set; }
}

public sealed record TimedPrompt(string Id, string Prompt, DateTimeOffset Deadline, int DurationSeconds, IReadOnlyList<string> Options);
public sealed record EngineOutput(List<string> Lines, TimedPrompt? TimedPrompt = null);
