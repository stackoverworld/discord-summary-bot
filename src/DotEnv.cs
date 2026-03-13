namespace DiscordSummaryBot;

public static class DotEnv
{
    public static void Load(string filePath = ".env")
    {
        var absolutePath = Path.GetFullPath(filePath);
        if (!File.Exists(absolutePath))
            return;

        foreach (var rawLine in File.ReadAllLines(absolutePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();

            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                value = value[1..^1];

            // Respect already-provided environment variables so production deployments
            // can override local .env files safely.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
