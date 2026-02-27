using System.Text.Json;
using Priory.Game;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
var dataRoot = Path.Combine(root, "data");
var saveRoot = Path.Combine(root, "saves");
Directory.CreateDirectory(saveRoot);

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("PRIORY: Blackpine");
Console.WriteLine("Yorkshire, 1403");
Console.WriteLine("Type 'help' for commands. Type 'save' anytime.\n");

var loader = new StoryLoader(dataRoot);
var story = loader.Load();
var saveCodec = new SaveCodec(Environment.GetEnvironmentVariable("PRIORY_SAVE_SECRET") ?? "DEV_ONLY_CHANGE_ME");
var engine = new GameEngine(story, saveRoot, saveCodec);

Console.Write("Resume code (or press Enter for new game): ");
var resumeCode = Console.ReadLine()?.Trim();
if (!string.IsNullOrWhiteSpace(resumeCode))
{
    var resumed = engine.TryResume(resumeCode, out var message);
    Console.WriteLine(message);
    if (!resumed)
    {
        engine.StartNewGame();
    }
}
else
{
    engine.StartNewGame();
}

while (true)
{
    if (engine.IsEnded)
    {
        Console.WriteLine("\n[End of current chapter. More content can be added via JSON files.]\n");
        break;
    }

    Console.Write("\n> ");
    var input = Console.ReadLine();
    if (input is null)
    {
        break;
    }

    var output = engine.HandleInput(input);
    foreach (var line in output.Lines)
    {
        Console.WriteLine(line);
    }

    if (output.TimedPrompt is { } timed)
    {
        var choice = TimedConsolePrompt.Ask(timed);
        var timedResult = engine.ResolveTimed(choice);
        foreach (var line in timedResult.Lines)
        {
            Console.WriteLine(line);
        }
    }
}
