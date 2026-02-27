using System.Text.Json;

namespace Priory.Game;

public sealed class StoryLoader
{
    private readonly string _dataRoot;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public StoryLoader(string dataRoot)
    {
        _dataRoot = dataRoot;
    }

    public StoryData Load()
    {
        var story = new StoryData();
        LoadScenes(story);
        LoadMenus(story);
        LoadTimed(story);
        LoadLifePaths(story);
        return story;
    }

    private void LoadScenes(StoryData story)
    {
        var path = Path.Combine(_dataRoot, "scenes");
        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            var scene = JsonSerializer.Deserialize<SceneDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading scene: {file}");
            story.Scenes[scene.Id] = scene;
        }
    }

    private void LoadMenus(StoryData story)
    {
        var file = Path.Combine(_dataRoot, "dialogue", "menus.json");
        var menus = JsonSerializer.Deserialize<List<MenuDef>>(File.ReadAllText(file), _jsonOptions)
            ?? new List<MenuDef>();
        foreach (var menu in menus)
        {
            story.Menus[menu.Id] = menu;
        }
    }

    private void LoadTimed(StoryData story)
    {
        var file = Path.Combine(_dataRoot, "dialogue", "timed.json");
        var timed = JsonSerializer.Deserialize<List<TimedDef>>(File.ReadAllText(file), _jsonOptions)
            ?? new List<TimedDef>();
        foreach (var t in timed)
        {
            story.Timed[t.Id] = t;
        }
    }

    private void LoadLifePaths(StoryData story)
    {
        var file = Path.Combine(_dataRoot, "lifepaths.json");
        var paths = JsonSerializer.Deserialize<Dictionary<string, LifePathDef>>(File.ReadAllText(file), _jsonOptions)
            ?? new Dictionary<string, LifePathDef>();
        story.LifePaths = paths;
    }
}
