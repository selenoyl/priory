namespace Priory.Game;

public enum Intent
{
    Unknown,
    Help,
    Look,
    Go,
    Talk,
    Examine,
    Take,
    Inventory,
    Status,
    Save,
    Quests,
    Quit,
    Party,
    Version,
    Virtues,
    Numeric
}

public sealed record ParsedInput(Intent Intent, string? Target = null, int Number = 0, string? Verb = null);

public static class Parser
{
    private static readonly Dictionary<string, Intent> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["help"] = Intent.Help,
        ["look"] = Intent.Look,
        ["observe"] = Intent.Look,
        ["examine"] = Intent.Examine,
        ["inspect"] = Intent.Examine,
        ["go"] = Intent.Go,
        ["walk"] = Intent.Go,
        ["travel"] = Intent.Go,
        ["move"] = Intent.Go,
        ["enter"] = Intent.Go,
        ["climb"] = Intent.Go,
        ["board"] = Intent.Go,
        ["mount"] = Intent.Go,
        ["leave"] = Intent.Go,
        ["depart"] = Intent.Go,
        ["talk"] = Intent.Talk,
        ["speak"] = Intent.Talk,
        ["converse"] = Intent.Talk,
        ["discuss"] = Intent.Talk,
        ["take"] = Intent.Take,
        ["grab"] = Intent.Take,
        ["inventory"] = Intent.Inventory,
        ["inv"] = Intent.Inventory,
        ["i"] = Intent.Inventory,
        ["status"] = Intent.Status,
        ["stats"] = Intent.Status,
        ["priory"] = Intent.Status,
        ["save"] = Intent.Save,
        ["quests"] = Intent.Quests,
        ["journal"] = Intent.Quests,
        ["quest"] = Intent.Quests,
        ["quit"] = Intent.Quit,
        ["exit"] = Intent.Quit,
        ["party"] = Intent.Party,
        ["companions"] = Intent.Party,
        ["version"] = Intent.Version,
        ["ver"] = Intent.Version,
        ["virtue"] = Intent.Virtues,
        ["virtues"] = Intent.Virtues,
        ["v"] = Intent.Virtues
    };

    public static ParsedInput Parse(string input)
    {
        var text = input.Trim();
        if (string.IsNullOrWhiteSpace(text)) return new(Intent.Unknown);
        if (int.TryParse(text, out var n)) return new(Intent.Numeric, Number: n, Verb: "#numeric");

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0];

        if (!Verbs.TryGetValue(verb, out var intent))
        {
            return new(Intent.Unknown, Verb: verb);
        }

        if (verb.Equals("exit", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
            intent = Intent.Go;

        string? target = null;
        if (parts.Length > 1)
        {
            var filtered = parts.Skip(1)
                .Where(x => x is not "to" and not "at" and not "the" and not "a" and not "an" and not "with" and not "in" and not "into" and not "on" and not "onto" and not "from" and not "out" and not "of")
                .ToArray();
            target = filtered.Length == 0 ? null : string.Join(' ', filtered);
        }

        return new(intent, target, Verb: verb.ToLowerInvariant());
    }
}
