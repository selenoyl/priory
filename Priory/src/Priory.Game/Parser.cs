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
    Save,
    Quit,
    Numeric
}

public sealed record ParsedInput(Intent Intent, string? Target = null, int Number = 0);

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
        ["talk"] = Intent.Talk,
        ["speak"] = Intent.Talk,
        ["converse"] = Intent.Talk,
        ["discuss"] = Intent.Talk,
        ["take"] = Intent.Take,
        ["grab"] = Intent.Take,
        ["inventory"] = Intent.Inventory,
        ["inv"] = Intent.Inventory,
        ["i"] = Intent.Inventory,
        ["save"] = Intent.Save,
        ["quit"] = Intent.Quit,
        ["exit"] = Intent.Quit
    };

    public static ParsedInput Parse(string input)
    {
        var text = input.Trim();
        if (string.IsNullOrWhiteSpace(text)) return new(Intent.Unknown);
        if (int.TryParse(text, out var n)) return new(Intent.Numeric, Number: n);

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0];

        if (!Verbs.TryGetValue(verb, out var intent))
        {
            return new(Intent.Unknown);
        }

        string? target = null;
        if (parts.Length > 1)
        {
            var filtered = parts.Skip(1)
                .Where(x => x is not "to" and not "at" and not "the" and not "a" and not "an")
                .ToArray();
            target = filtered.Length == 0 ? null : string.Join(' ', filtered);
        }

        return new(intent, target);
    }
}
