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
        LoadSceneFiles(story);
        LoadMenuFiles(story);
        LoadTimedFiles(story);
        LoadLifePathFiles(story);
        LoadQuestFiles(story);
        LoadItemFiles(story);
        LoadShopFiles(story);
        return story;
    }

    private static IEnumerable<string> JsonFiles(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.OrdinalIgnoreCase));
    }

    private void LoadSceneFiles(StoryData story)
    {
        foreach (var file in JsonFiles(Path.Combine(_dataRoot, "scenes")))
        {
            var scene = JsonSerializer.Deserialize<SceneDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading scene: {file}");
            story.Scenes[scene.Id] = scene;
        }
    }

    private void LoadMenuFiles(StoryData story)
    {
        foreach (var file in JsonFiles(Path.Combine(_dataRoot, "dialogue", "menus")))
        {
            var menu = JsonSerializer.Deserialize<MenuDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading menu: {file}");
            story.Menus[menu.Id] = menu;
        }
    }

    private void LoadTimedFiles(StoryData story)
    {
        foreach (var file in JsonFiles(Path.Combine(_dataRoot, "dialogue", "timed")))
        {
            var t = JsonSerializer.Deserialize<TimedDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading timed event: {file}");
            story.Timed[t.Id] = t;
        }
    }

    private void LoadLifePathFiles(StoryData story)
    {
        foreach (var file in JsonFiles(Path.Combine(_dataRoot, "lifepaths")))
        {
            var lp = JsonSerializer.Deserialize<LifePathDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading life path: {file}");
            var id = Path.GetFileNameWithoutExtension(file);
            story.LifePaths[id] = lp;
        }
    }

    private void LoadQuestFiles(StoryData story)
    {
        foreach (var file in JsonFiles(Path.Combine(_dataRoot, "quests")))
        {
            var q = JsonSerializer.Deserialize<QuestDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading quest: {file}");
            story.Quests[q.Id] = q;
        }
    }

    private void LoadItemFiles(StoryData story)
    {
        foreach (var file in JsonFiles(Path.Combine(_dataRoot, "items")))
        {
            var i = JsonSerializer.Deserialize<ItemDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading item: {file}");
            story.Items[i.Id] = i;
        }
    }

    private void LoadShopFiles(StoryData story)
    {
        foreach (var file in JsonFiles(Path.Combine(_dataRoot, "shops")))
        {
            var s = JsonSerializer.Deserialize<ShopDef>(File.ReadAllText(file), _jsonOptions)
                ?? throw new InvalidOperationException($"Failed loading shop: {file}");
            story.Shops[s.Id] = s;
        }
    }
}
