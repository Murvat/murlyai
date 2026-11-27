using System.Text.Json;

namespace Murly;

public interface ITutor
{
    Task<string> ReplyAsync(Lesson lesson, string mode, IReadOnlyList<Turn> history,
        string message, CancellationToken cancellation);
}

public sealed class TutorUnavailableException(string message) : Exception(message);

public sealed class OpenAiTutor(HttpClient http, IConfiguration config, IHostEnvironment environment) : ITutor
{
    public async Task<string> ReplyAsync(Lesson lesson, string mode, IReadOnlyList<Turn> history,
        string message, CancellationToken cancellation)
    {
        var key = config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new TutorUnavailableException("The OpenAI API key is missing.");

        var instructions = await File.ReadAllTextAsync(
            Path.Combine(environment.ContentRootPath, "Instructions", $"{mode}.md"), cancellation);
        var input = new List<object>
        {
            new { role = "user", content = JsonSerializer.Serialize(new
            {
                lesson.Title, lesson.TargetLanguage, lesson.OpeningContent, lesson.Examples
            }) }
        };
        foreach (var turn in history)
        {
            input.Add(new { role = "user", content = turn.Message });
            input.Add(new { role = "assistant", content = turn.Reply });
        }
        input.Add(new { role = "user", content = message });

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses");
        request.Headers.Authorization = new("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            model = config["OpenAI:Model"],
            instructions,
            input,
            store = false,
            max_output_tokens = 800
        });
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new TutorUnavailableException($"OpenAI returned HTTP {(int)response.StatusCode}.");

        using var payload = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellation), cancellationToken: cancellation);
        if (payload.RootElement.GetProperty("status").GetString() != "completed")
            throw new TutorUnavailableException("OpenAI did not complete the response.");

        var parts = payload.RootElement.GetProperty("output").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "message")
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(content => content.GetProperty("type").GetString() == "output_text")
            .Select(content => content.GetProperty("text").GetString());
        var reply = string.Join("\n", parts).Trim();
        return reply.Length > 0 ? reply : throw new TutorUnavailableException("OpenAI returned no text.");
    }
}

public sealed class DemoTutor : ITutor
{
    public Task<string> ReplyAsync(Lesson lesson, string mode, IReadOnlyList<Turn> history,
        string message, CancellationToken cancellation)
    {
        var example = lesson.Examples.FirstOrDefault();
        if (example is null)
            return Task.FromResult("What would you like to try from this lesson?");
        if (mode == "learning")
            return Task.FromResult($"Try **{example.Phrase}**. It means “{example.Translation}” in {lesson.TargetLanguage}. When might you use it?");

        var previous = lesson.Examples[history.Count == 0 ? 0 : (history.Count - 1) % lesson.Examples.Count];
        var next = lesson.Examples[history.Count % lesson.Examples.Count];
        if (history.Count == 0)
            return Task.FromResult($"How would you say “{next.Translation}” in {lesson.TargetLanguage}?");
        var attempt = message.Trim().Trim('¿', '¡', '?', '!', '.');
        var answer = previous.Phrase.Trim('¿', '¡', '?', '!', '.');
        var feedback = string.Equals(attempt, answer, StringComparison.OrdinalIgnoreCase)
            ? "Correct!"
            : $"One way to say that is **{previous.Phrase}**.";
        return Task.FromResult($"{feedback}\n\nHow would you say “{next.Translation}”?");
    }
}
