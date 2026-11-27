using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Murly.Tests;

public sealed class ChatTests(MurlyFixture fixture) : IClassFixture<MurlyFixture>
{
    private const string Learning = "/api/lessons/say-hello/chats/learning";
    private const string Practice = "/api/lessons/say-hello/chats/practice";

    [Fact]
    public async Task TutorsUseStoredLessonAndModeSpecificHistory()
    {
        using var learner = fixture.CreateLearner("test-learner");
        using var other = fixture.CreateLearner("other-learner");
        using var anonymous = fixture.CreateLearner(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Learning)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(anonymous, Learning, new(Guid.NewGuid(), 0, "hello"))).StatusCode);

        var starting = await GetJsonAsync(learner, Learning);
        Assert.Contains("Start with three greetings", starting.GetProperty("lesson").GetProperty("openingContent").GetString());
        Assert.Empty(starting.GetProperty("turns").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(learner, Learning, new(Guid.NewGuid(), 0, "Explain hola"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(learner, Practice, new(Guid.NewGuid(), 0, "Quiz me"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(learner, Learning, new(Guid.NewGuid(), 1, "Show another greeting"))).StatusCode);

        var learningRequest = fixture.Transport.Requests["Show another greeting"];
        var practiceRequest = fixture.Transport.Requests["Quiz me"];
        Assert.Contains("Learning mode", learningRequest.GetProperty("instructions").GetString());
        Assert.Contains("Practice mode", practiceRequest.GetProperty("instructions").GetString());
        Assert.Contains("Spanish", learningRequest.GetProperty("input")[0].GetProperty("content").GetString());
        Assert.Equal("Explain hola", learningRequest.GetProperty("input")[1].GetProperty("content").GetString());
        Assert.Equal("assistant", learningRequest.GetProperty("input")[2].GetProperty("role").GetString());
        Assert.Equal(2, practiceRequest.GetProperty("input").GetArrayLength());
        Assert.Equal(2, (await GetJsonAsync(learner, Learning)).GetProperty("revision").GetInt32());
        Assert.Equal(1, (await GetJsonAsync(learner, Practice)).GetProperty("revision").GetInt32());
        Assert.Empty((await GetJsonAsync(other, Learning)).GetProperty("turns").EnumerateArray());
    }

    [Fact]
    public async Task TimeoutLeavesHistoryUntouchedAndRetryIsIdempotent()
    {
        using var learner = fixture.CreateLearner("retry-learner");
        var request = new SendMessage(Guid.NewGuid(), 0, "Timeout once");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(learner, Learning, request)).StatusCode);
        Assert.Empty((await GetJsonAsync(learner, Learning)).GetProperty("turns").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(learner, Learning, request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(learner, Learning, request)).StatusCode);
        Assert.Single((await GetJsonAsync(learner, Learning)).GetProperty("turns").EnumerateArray());
        Assert.Equal(2, fixture.Transport.CallCounts["Timeout once"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ConcurrentSendsSaveOnlyOneExchange(int revision)
    {
        using var learner = fixture.CreateLearner($"concurrent-learner-{revision}");
        if (revision == 1)
            Assert.Equal(HttpStatusCode.OK,
                (await SendAsync(learner, Learning, new(Guid.NewGuid(), 0, "Start here"))).StatusCode);

        fixture.Transport.PrepareRace();
        var results = await Task.WhenAll(
            SendAsync(learner, Learning, new(Guid.NewGuid(), revision, "Race A")),
            SendAsync(learner, Learning, new(Guid.NewGuid(), revision, "Race B")));
        Assert.Contains(results, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Contains(results, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(revision + 1, (await GetJsonAsync(learner, Learning)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task CurriculumUpdatesKeepHistoryAndInvalidImportsWriteNothing()
    {
        using var learner = fixture.CreateLearner("curriculum-learner");
        const string coffee = "/api/lessons/order-a-coffee/chats/learning";
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(learner, coffee, new(Guid.NewGuid(), 0, "Please explain"))).StatusCode);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MurlyDb>();
        var path = Path.Combine(fixture.DirectoryPath, "import.json");
        const string valid = """
            {"id":"order-a-coffee","title":"At the café","targetLanguage":"Spanish","displayOrder":3,
            "openingContent":"A revised lesson.","examples":[{"phrase":"Un té.","translation":"A tea."}]}
            """;
        await File.WriteAllTextAsync(path, $"[{valid}, {valid}]");
        await Assert.ThrowsAsync<InvalidDataException>(() => Curriculum.ImportAsync(db, path, default));
        Assert.Equal("Order a coffee", (await GetJsonAsync(learner, coffee)).GetProperty("lesson").GetProperty("title").GetString());

        await File.WriteAllTextAsync(path, $"[{valid}]");
        Assert.Equal(1, await Curriculum.ImportAsync(db, path, default));
        var chat = await GetJsonAsync(learner, coffee);
        Assert.Equal("At the café", chat.GetProperty("lesson").GetProperty("title").GetString());
        Assert.Equal("Un té.", chat.GetProperty("lesson").GetProperty("examples")[0].GetProperty("phrase").GetString());
        Assert.Single(chat.GetProperty("turns").EnumerateArray());
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, SendMessage message) =>
        client.PostAsJsonAsync(path + "/messages", message);

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path) =>
        await client.GetFromJsonAsync<JsonElement>(path);
}

public sealed class MurlyFixture : WebApplicationFactory<Program>
{
    public string DirectoryPath { get; } =
        Directory.CreateTempSubdirectory("murly-tests-").FullName;
    public RecordingOpenAi Transport { get; } = new();

    public HttpClient CreateLearner(string? subject)
    {
        var client = CreateClient();
        if (subject is not null)
            client.DefaultRequestHeaders.Add("Test-Learner", subject);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key",
                ["Identity:Authority"] = "https://identity.example.invalid",
                ["Identity:ClientId"] = "test-app"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<MurlyDb>>();
            services.AddDbContext<MurlyDb>(options =>
                options.UseSqlite($"Data Source={Path.Combine(DirectoryPath, "test.db")}"));
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthentication>(
                "test-learner", _ => { });
            services.PostConfigure<AuthenticationOptions>(options => options.DefaultScheme = "test-learner");
            services.AddHttpClient<ITutor, OpenAiTutor>().ConfigurePrimaryHttpMessageHandler(() => Transport);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MurlyDb>();
        db.Database.EnsureCreated();
        Curriculum.ImportAsync(db, Path.Combine(AppContext.BaseDirectory, "Curriculum", "demo.json"), default)
            .GetAwaiter().GetResult();
        return host;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var learner = Request.Headers["Test-Learner"].ToString();
        if (learner.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new("iss", "test"), new("sub", learner)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new(identity), Scheme.Name)));
    }
}

public sealed class RecordingOpenAi : HttpMessageHandler
{
    public ConcurrentDictionary<string, JsonElement> Requests { get; } = new();
    public ConcurrentDictionary<string, int> CallCounts { get; } = new();
    private TaskCompletionSource _race = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public void PrepareRace()
    {
        _race = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _arrived = 0;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var message = payload.GetProperty("input").EnumerateArray().Last().GetProperty("content").GetString()!;
        Requests[message] = payload;
        var count = CallCounts.AddOrUpdate(message, 1, (_, previous) => previous + 1);
        if (message == "Timeout once" && count == 1)
            throw new TaskCanceledException("Simulated OpenAI timeout.");
        if (message.StartsWith("Race ", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref _arrived) == 2)
                _race.TrySetResult();
            await _race.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                status = "completed",
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "Test reply" } } } }
            })
        };
    }
}
