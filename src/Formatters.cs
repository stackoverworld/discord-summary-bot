using System.Text;

namespace DiscordSummaryBot;

public static class Formatters
{
    public const int DiscordMessageCharacterLimit = 2000;

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} ч {duration.Minutes} мин {duration.Seconds} сек";

        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes} мин {duration.Seconds} сек";

        return $"{Math.Max(0, duration.Seconds)} сек";
    }

    public static string BuildDiscordSummary(
        string voiceChannelName,
        TimeSpan duration,
        MeetingSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {summary.Title}");
        builder.AppendLine();
        builder.AppendLine($"Канал: {voiceChannelName}");
        builder.AppendLine($"Длительность: {FormatDuration(duration)}");
        builder.AppendLine();
        builder.AppendLine("## Кратко");
        builder.AppendLine(summary.Summary);
        builder.AppendLine();
        builder.AppendLine("## Решения");
        AppendBulletList(builder, summary.Decisions);
        builder.AppendLine();
        builder.AppendLine("## Что сделать");
        if (summary.ActionItems.Count == 0)
        {
            builder.AppendLine("- Нет явных задач.");
        }
        else
        {
            foreach (var actionItem in summary.ActionItems)
            {
                builder.AppendLine(
                    $"- {actionItem.Task}. Ответственный: {actionItem.Owner ?? "не указан"}. Срок: {actionItem.Deadline ?? "не указан"}.");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Блокеры");
        AppendBulletList(builder, summary.Blockers);
        builder.AppendLine();
        builder.AppendLine("## Открытые вопросы");
        AppendBulletList(builder, summary.OpenQuestions);

        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyList<string> SplitForDiscordMessages(string content, int maxLength = DiscordMessageCharacterLimit)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [string.Empty];

        var normalized = content.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxLength)
            return [normalized];

        var chunks = new List<string>();
        var blocks = SplitIntoBlocks(normalized);
        var current = new StringBuilder();

        foreach (var block in blocks)
        {
            AppendBlock(chunks, current, block, maxLength, "\n\n");
        }

        FlushCurrent(chunks, current);
        return chunks;
    }

    public static string BuildTranscriptMarkdown(string sessionId, IReadOnlyList<TranscriptEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Transcript {sessionId}");
        builder.AppendLine();

        foreach (var entry in entries)
        {
            builder.AppendLine($"- [{entry.StartedAt:HH:mm:ss}] **{entry.DisplayName}**: {entry.Text}");
        }

        return builder.ToString();
    }

    public static string BuildSummaryMarkdown(MeetingSummary summary, IReadOnlyList<SummaryCheckpoint> checkpoints)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {summary.Title}");
        builder.AppendLine();
        builder.AppendLine("## Кратко");
        builder.AppendLine(summary.Summary);
        builder.AppendLine();
        builder.AppendLine("## Решения");
        AppendBulletList(builder, summary.Decisions);
        builder.AppendLine();
        builder.AppendLine("## Что сделать");

        if (summary.ActionItems.Count == 0)
        {
            builder.AppendLine("- Нет явных задач.");
        }
        else
        {
            foreach (var item in summary.ActionItems)
            {
                builder.AppendLine(
                    $"- {item.Task}. Ответственный: {item.Owner ?? "не указан"}. Срок: {item.Deadline ?? "не указан"}.");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Блокеры");
        AppendBulletList(builder, summary.Blockers);
        builder.AppendLine();
        builder.AppendLine("## Открытые вопросы");
        AppendBulletList(builder, summary.OpenQuestions);
        builder.AppendLine();
        builder.AppendLine("## Важные детали");
        AppendBulletList(builder, summary.NotablePoints);
        builder.AppendLine();
        builder.AppendLine("## Промежуточные сводки");

        if (checkpoints.Count == 0)
        {
            builder.AppendLine("- Нет.");
        }
        else
        {
            foreach (var checkpoint in checkpoints)
            {
                builder.AppendLine($"- {checkpoint.CreatedAt:O}: {checkpoint.Overview}");
            }
        }

        return builder.ToString();
    }

    private static void AppendBulletList(StringBuilder builder, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            builder.AppendLine("- Нет.");
            return;
        }

        foreach (var item in items)
            builder.AppendLine($"- {item}");
    }

    private static void AppendBlock(
        List<string> chunks,
        StringBuilder current,
        string block,
        int maxLength,
        string separator)
    {
        if (string.IsNullOrEmpty(block))
        {
            TryAppend(chunks, current, string.Empty, maxLength, separator);
            return;
        }

        if (block.Length <= maxLength)
        {
            TryAppend(chunks, current, block, maxLength, separator);
            return;
        }

        var lines = block.Split('\n');
        foreach (var line in lines)
        {
            if (line.Length <= maxLength)
            {
                TryAppend(chunks, current, line, maxLength, "\n");
                continue;
            }

            var words = line.Split(' ', StringSplitOptions.None);
            foreach (var word in words)
            {
                if (word.Length <= maxLength)
                {
                    TryAppend(chunks, current, word, maxLength, " ");
                    continue;
                }

                FlushCurrent(chunks, current);
                for (var index = 0; index < word.Length; index += maxLength)
                {
                    var length = Math.Min(maxLength, word.Length - index);
                    chunks.Add(word.Substring(index, length));
                }
            }
        }
    }

    private static void TryAppend(
        List<string> chunks,
        StringBuilder current,
        string text,
        int maxLength,
        string separator)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var candidate = current.Length == 0
            ? text
            : current + separator + text;

        if (candidate.Length <= maxLength)
        {
            if (current.Length > 0)
                current.Append(separator);

            current.Append(text);
            return;
        }

        FlushCurrent(chunks, current);
        current.Append(text);
    }

    private static void FlushCurrent(List<string> chunks, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        chunks.Add(current.ToString());
        current.Clear();
    }

    private static IReadOnlyList<string> SplitIntoBlocks(string content)
    {
        var lines = content.Split('\n');
        var blocks = new List<string>();
        var current = new List<string>();

        void Flush()
        {
            if (current.Count == 0)
                return;

            blocks.Add(string.Join("\n", current).Trim());
            current.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var isHeading = line.StartsWith("# ");
            var isSectionHeading = line.StartsWith("## ");

            if (isSectionHeading)
            {
                Flush();
                current.Add(line);
                continue;
            }

            if (isHeading)
            {
                Flush();
                current.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                    current.Add(string.Empty);

                continue;
            }

            current.Add(line);
        }

        Flush();
        return blocks.Where(block => !string.IsNullOrWhiteSpace(block)).ToArray();
    }
}
