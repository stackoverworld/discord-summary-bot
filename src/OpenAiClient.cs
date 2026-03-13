using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiscordSummaryBot;

public sealed class OpenAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _summaryHttpClient;
    private readonly HttpClient _transcriptionHttpClient;
    private readonly AppConfig _config;
    private readonly BotLogger _logger;

    public OpenAiClient(
        HttpClient summaryHttpClient,
        HttpClient transcriptionHttpClient,
        AppConfig config,
        BotLogger logger)
    {
        _summaryHttpClient = summaryHttpClient;
        _transcriptionHttpClient = transcriptionHttpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(string filePath, string displayName, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(_config.TranscriptionModel), "model");
        content.Add(
            new StringContent("Transcribe exactly what is said. Preserve tasks, decisions, dates, names, and numbers."),
            "prompt");
        if (!string.IsNullOrWhiteSpace(_config.TranscriptionLanguage))
            content.Add(new StringContent(_config.TranscriptionLanguage), "language");

        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var response = await _transcriptionHttpClient.PostAsync("audio/transcriptions", content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Transcription failed for '{displayName}': {payload}");

        var json = JsonNode.Parse(payload)?.AsObject();
        return json?["text"]?.GetValue<string>()?.Trim() ?? string.Empty;
    }

    public async Task<SummaryCheckpointPayload> BuildCheckpointAsync(
        string sessionTitle,
        IReadOnlyList<SummaryCheckpoint> checkpoints,
        IReadOnlyList<TranscriptEntry> entries,
        CancellationToken cancellationToken)
    {
        var response = await CreateSummaryJsonAsync(
            BuildCheckpointInput(sessionTitle, checkpoints, entries),
            BuildCheckpointSchema(),
            cancellationToken);

        return JsonSerializer.Deserialize<SummaryCheckpointPayload>(response, JsonOptions)
               ?? throw new InvalidOperationException("Checkpoint summary response was empty.");
    }

    public async Task<MeetingSummary> BuildFinalSummaryAsync(
        string sessionTitle,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<TranscriptEntry> entries,
        IReadOnlyList<SummaryCheckpoint> checkpoints,
        CancellationToken cancellationToken)
    {
        var response = await CreateSummaryJsonAsync(
            BuildFinalSummaryInput(sessionTitle, startedAt, endedAt, entries, checkpoints),
            BuildFinalSummarySchema(),
            cancellationToken);

        return JsonSerializer.Deserialize<MeetingSummary>(response, JsonOptions)
               ?? throw new InvalidOperationException("Final summary response was empty.");
    }

    private async Task<string> CreateSummaryJsonAsync(
        string userInput,
        JsonObject schema,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = _config.OpenRouterModel,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] =
                        "You summarize meeting transcripts into factual operational notes in Russian. Write concise, natural Russian. Never invent owners or deadlines. Use null when owner or deadline is not explicit. Do not include verbatim quotes from the transcript unless absolutely necessary. Keep the overall summary mostly impersonal, but when a specific person materially proposed a decision, raised a blocker, confirmed a direction, or took responsibility for a task, mention that person's display name in the relevant bullet. Do not turn the report into a speaker-by-speaker transcript."
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = BuildStructuredOutputPrompt(schema, userInput)
                }
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_object"
            },
            ["temperature"] = _config.OpenRouterTemperature
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _summaryHttpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter summary call failed: {payload}");

        var parsed = JsonNode.Parse(payload)?.AsObject()
                     ?? throw new InvalidOperationException("OpenRouter summary payload was invalid JSON.");

        var text = parsed["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        return text?.Trim()
               ?? throw new InvalidOperationException($"OpenRouter summary payload did not contain message content: {payload}");
    }

    private static string BuildStructuredOutputPrompt(JsonObject schema, string userInput)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Return exactly one JSON object that matches this JSON Schema.");
        builder.AppendLine("Do not wrap it in markdown fences.");
        builder.AppendLine();
        builder.AppendLine(schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        builder.AppendLine();
        builder.Append(userInput);
        return builder.ToString();
    }

    private static string BuildCheckpointInput(
        string sessionTitle,
        IReadOnlyList<SummaryCheckpoint> checkpoints,
        IReadOnlyList<TranscriptEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Session: {sessionTitle}");
        builder.AppendLine();
        builder.AppendLine("Return all fields in Russian.");
        builder.AppendLine("Use names selectively: mention a person's display name only when they materially proposed something, raised a blocker, confirmed a decision, or clearly own an action.");
        builder.AppendLine("Do not turn the output into a speaker-by-speaker transcript.");
        builder.AppendLine();
        builder.AppendLine("Existing checkpoints:");
        if (checkpoints.Count == 0)
        {
            builder.AppendLine("None");
        }
        else
        {
            foreach (var checkpoint in checkpoints)
            {
                builder.AppendLine(
                    $"{checkpoint.CreatedAt:O} | Overview: {checkpoint.Overview} | Decisions: {string.Join("; ", checkpoint.Decisions)} | Action items: {string.Join("; ", checkpoint.ActionItems)} | Blockers: {string.Join("; ", checkpoint.Blockers)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("New transcript entries:");
        foreach (var entry in entries)
            builder.AppendLine($"[{entry.StartedAt:O} - {entry.EndedAt:O}] {entry.DisplayName}: {entry.Text}");

        return builder.ToString();
    }

    private static string BuildFinalSummaryInput(
        string sessionTitle,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<TranscriptEntry> entries,
        IReadOnlyList<SummaryCheckpoint> checkpoints)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Session: {sessionTitle}");
        builder.AppendLine($"Started at: {startedAt:O}");
        builder.AppendLine($"Ended at: {endedAt:O}");
        builder.AppendLine();
        builder.AppendLine("Return all fields in Russian.");
        builder.AppendLine("Do not quote the transcript verbatim. Provide only a concise summary, decisions, tasks, blockers and open questions.");
        builder.AppendLine("Keep the summary mostly impersonal.");
        builder.AppendLine("Mention a person's display name only when that attribution adds value: they proposed a decision, raised a blocker, confirmed a direction, or explicitly took a task.");
        builder.AppendLine("If no meaningful attribution is needed, keep the wording neutral.");
        builder.AppendLine();
        builder.AppendLine("Checkpoints:");

        if (checkpoints.Count == 0)
        {
            builder.AppendLine("None");
        }
        else
        {
            foreach (var checkpoint in checkpoints)
            {
                builder.AppendLine(
                    $"{checkpoint.CreatedAt:O} | Overview: {checkpoint.Overview} | Decisions: {string.Join("; ", checkpoint.Decisions)} | Action items: {string.Join("; ", checkpoint.ActionItems)} | Blockers: {string.Join("; ", checkpoint.Blockers)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Transcript:");
        foreach (var entry in entries)
            builder.AppendLine($"[{entry.StartedAt:O} - {entry.EndedAt:O}] {entry.DisplayName}: {entry.Text}");

        return builder.ToString();
    }

    private static JsonObject BuildCheckpointSchema() =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["overview"] = new JsonObject { ["type"] = "string" },
                ["decisions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["actionItems"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["blockers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            },
            ["required"] = new JsonArray("overview", "decisions", "actionItems", "blockers")
        };

    private static JsonObject BuildFinalSummarySchema() =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string" },
                ["summary"] = new JsonObject { ["type"] = "string" },
                ["decisions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["actionItems"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["task"] = new JsonObject { ["type"] = "string" },
                            ["owner"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                            ["deadline"] = new JsonObject { ["type"] = new JsonArray("string", "null") }
                        },
                        ["required"] = new JsonArray("task", "owner", "deadline")
                    }
                },
                ["blockers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["openQuestions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["notablePoints"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            },
            ["required"] = new JsonArray("title", "summary", "decisions", "actionItems", "blockers", "openQuestions", "notablePoints")
        };
}

public sealed record SummaryCheckpointPayload(
    string Overview,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> ActionItems,
    IReadOnlyList<string> Blockers);
