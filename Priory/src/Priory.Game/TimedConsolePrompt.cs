namespace Priory.Game;

public static class TimedConsolePrompt
{
    public static int Ask(TimedPrompt prompt)
    {
        while (DateTimeOffset.UtcNow < prompt.Deadline)
        {
            var remaining = prompt.Deadline - DateTimeOffset.UtcNow;
            RenderBar(remaining.TotalSeconds, (prompt.Deadline - DateTimeOffset.UtcNow).TotalSeconds);

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (char.IsDigit(key.KeyChar))
                {
                    Console.WriteLine();
                    return int.Parse(key.KeyChar.ToString());
                }
            }

            Thread.Sleep(80);
        }

        Console.WriteLine();
        return 0;
    }

    private static void RenderBar(double remaining, double _)
    {
        const int width = 20;
        var maxSeconds = 6.0;
        var ratio = Math.Clamp(remaining / maxSeconds, 0, 1);
        var filled = (int)Math.Round(ratio * width);
        var bar = new string('█', filled) + new string('░', width - filled);
        Console.Write($"\r⏳ [{bar}] {Math.Max(0, remaining):0.0}s ");
    }
}
