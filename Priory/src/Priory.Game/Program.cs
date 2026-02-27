using Priory.Game;

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
var localDataRoot = Path.Combine(projectRoot, "data");
var localSaveRoot = Path.Combine(projectRoot, "saves");

var dataRoot = Directory.Exists(localDataRoot) ? localDataRoot : Path.Combine(AppContext.BaseDirectory, "data");
var saveRoot = Directory.Exists(localSaveRoot) ? localSaveRoot : Path.Combine(AppContext.BaseDirectory, "saves");
Directory.CreateDirectory(saveRoot);

var loader = new StoryLoader(dataRoot);
var story = loader.Load();
var saveCodec = new SaveCodec(Environment.GetEnvironmentVariable("PRIORY_SAVE_SECRET") ?? "DEV_ONLY_CHANGE_ME");

if (string.Equals(Environment.GetEnvironmentVariable("PRIORY_SERVER_MODE"), "true", StringComparison.OrdinalIgnoreCase))
{
    await ServerHost.RunAsync(args, story, saveRoot, saveCodec);
    return;
}

RunCli(story, saveRoot, saveCodec);

static void RunCli(StoryData story, string saveRoot, SaveCodec saveCodec)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("PRIORY: Blackpine");
    Console.WriteLine("Yorkshire, 1403");
    Console.WriteLine("Type 'help' for commands. Type 'save' anytime.\n");

    var engine = new GameEngine(story, saveRoot, saveCodec);

    Console.Write("Resume code (or press Enter for new game): ");
    var resumeCode = Console.ReadLine()?.Trim();
    if (!string.IsNullOrWhiteSpace(resumeCode))
    {
        var resumed = engine.TryResume(resumeCode, out var message);
        Console.WriteLine(message);
        if (!resumed)
        {
            SetupNewGame(engine);
        }
    }
    else
    {
        SetupNewGame(engine);
    }

    while (true)
    {
        if (engine.IsEnded)
        {
            Console.WriteLine("\n[End of current arc. Add more content modules in JSON to continue.]\n");
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
}

static void SetupNewGame(GameEngine engine)
{
    var playerName = PromptRequired("Choose your character name: ");

    Console.WriteLine("Choose mode:");
    Console.WriteLine("  1) Solo journey");
    Console.WriteLine("  2) Create party");
    Console.WriteLine("  3) Join party");
    Console.Write("Selection: ");

    var mode = Console.ReadLine()?.Trim();
    switch (mode)
    {
        case "2":
            var partyCode = engine.CreateParty();
            Console.WriteLine($"Party created. Share this code with a friend: {partyCode}");
            break;
        case "3":
            Console.Write("Enter party code: ");
            var joinCode = Console.ReadLine()?.Trim() ?? "";
            if (!engine.JoinParty(joinCode, out var joinMessage))
            {
                Console.WriteLine(joinMessage);
                Console.WriteLine("Continuing in solo mode.");
                engine.UseSoloMode();
            }
            else
            {
                Console.WriteLine(joinMessage);
            }
            break;
        default:
            engine.UseSoloMode();
            break;
    }

    engine.StartNewGame(playerName);
}

static string PromptRequired(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        var value = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(value)) return value;
        Console.WriteLine("A name is required.");
    }
}
