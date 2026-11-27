using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Murly;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var demo = builder.Environment.IsDevelopment() && configuration.GetValue<bool>("Demo:Enabled");
var command = args.Length == 0 ? null : args[0] switch
{
    "migrate" => "migrate",
    "import-curriculum" => "import-curriculum",
    _ => null
};

builder.Services.AddDbContext<MurlyDb>(options =>
{
    var connection = configuration.GetConnectionString("Murly");
    if (demo)
        options.UseSqlite(connection ?? "Data Source=murly-demo.db;Default Timeout=30");
    else if (connection is not null)
        options.UseSqlServer(connection);
    else
        throw new InvalidOperationException("Set ConnectionStrings:Murly to the Azure SQL connection string.");
});

if (demo && configuration.GetValue<bool>("Demo:MockTutor"))
    builder.Services.AddSingleton<ITutor, DemoTutor>();
else
    builder.Services.AddHttpClient<ITutor, OpenAiTutor>(client =>
    {
        client.BaseAddress = new Uri("https://api.openai.com/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });

var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = demo ? "demo" : CookieAuthenticationDefaults.AuthenticationScheme;
});
if (demo)
    authentication.AddScheme<AuthenticationSchemeOptions, DemoAuthentication>("demo", _ => { });
else if (command is null)
{
    authentication.AddCookie(options =>
    {
        options.Cookie.Name = "__Host-murly";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
    authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.Authority = configuration["Identity:Authority"];
        options.ClientId = configuration["Identity:ClientId"] ?? "";
        options.ClientSecret = configuration["Identity:ClientSecret"];
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.TokenValidationParameters.NameClaimType = "name";
    });
}
builder.Services.AddAuthorization(options => options.DefaultPolicy =
    new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().RequireClaim("iss").RequireClaim("sub").Build());
builder.Services.AddAntiforgery();
builder.Services.AddProblemDetails();

var app = builder.Build();

if (command is not null || demo)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MurlyDb>();
    if (demo)
    {
        await db.Database.EnsureCreatedAsync();
        if (command is null && !await db.Lessons.AnyAsync())
            await Curriculum.ImportAsync(db, Path.Combine(app.Environment.ContentRootPath, "Curriculum", "demo.json"), default);
    }
    else if (command == "migrate")
        await db.Database.MigrateAsync();

    if (command == "import-curriculum")
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: import-curriculum <curriculum.json>");
        var count = await Curriculum.ImportAsync(db, Path.GetFullPath(args[1]), default);
        Console.WriteLine($"Imported {count} lesson(s).");
    }
    if (command is not null)
        return;
}

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/session", (HttpContext context, IAntiforgery antiforgery) =>
{
    var signedIn = context.User.Identity?.IsAuthenticated == true;
    return Results.Ok(new
    {
        signedIn,
        name = signedIn ? context.User.Identity?.Name ?? "Learner" : null,
        demo,
        logoutToken = signedIn && !demo ? antiforgery.GetAndStoreTokens(context).RequestToken : null
    });
});
if (!demo)
{
    app.MapGet("/auth/login", () =>
        Results.Challenge(new AuthenticationProperties { RedirectUri = "/" },
            [OpenIdConnectDefaults.AuthenticationScheme]));
    app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        if (!await antiforgery.IsRequestValidAsync(context))
            return Results.BadRequest();
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }).RequireAuthorization();
}

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/lessons", async (MurlyDb db, CancellationToken cancellation) =>
    await db.Lessons.OrderBy(lesson => lesson.DisplayOrder).ThenBy(lesson => lesson.Id)
        .Select(lesson => new { lesson.Id, lesson.Title, lesson.TargetLanguage })
        .ToListAsync(cancellation));
api.MapGet("/lessons/{lessonId}/chats/{mode}", ChatEndpoints.GetAsync);
api.MapPost("/lessons/{lessonId}/chats/{mode}/messages", ChatEndpoints.SendAsync);
app.MapFallback("/api/{*path}", () => Results.NotFound());
app.MapFallback("/auth/{*path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();

public partial class Program;

namespace Murly
{
    public sealed class DemoAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new("iss", "local"), new("sub", "demo"), new(ClaimTypes.Name, "Demo learner")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new(identity), Scheme.Name)));
        }
    }

    public sealed record SendMessage(Guid RequestId, int ExpectedRevision, string? Message);

    public static class ChatEndpoints
    {
        public static async Task<IResult> GetAsync(string lessonId, string mode, ClaimsPrincipal learner,
            MurlyDb db, CancellationToken cancellation)
        {
            if (!ValidMode(mode))
                return Results.NotFound();
            var lesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == lessonId, cancellation);
            if (lesson is null)
                return Results.NotFound();
            var chat = await FindChat(db, LearnerKey(learner), lesson.Id, mode, cancellation);
            var turns = chat is null ? [] : await db.Turns.AsNoTracking()
                .Where(turn => turn.ChatId == chat.Id && turn.Revision <= chat.Revision)
                .OrderBy(turn => turn.Revision).ToListAsync(cancellation);
            return Results.Ok(new { lesson, revision = chat?.Revision ?? 0, turns });
        }

        public static async Task<IResult> SendAsync(string lessonId, string mode, SendMessage input,
            ClaimsPrincipal learner, MurlyDb db, ITutor tutor, ILoggerFactory logger, CancellationToken cancellation)
        {
            if (!ValidMode(mode))
                return Results.NotFound();
            var message = input.Message?.Trim();
            if (input.RequestId == Guid.Empty || input.ExpectedRevision < 0 ||
                string.IsNullOrEmpty(message) || message.Length > 4000)
                return Results.Problem("Enter a message of 1 to 4,000 characters.", statusCode: 400);

            var lesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == lessonId, cancellation);
            if (lesson is null)
                return Results.NotFound();
            var learnerId = LearnerKey(learner);
            var chat = await FindChat(db, learnerId, lesson.Id, mode, cancellation);
            var existing = await FindTurn(db, chat, input.RequestId, cancellation);
            if (existing is not null)
                return Replay(existing, input, message);
            if ((chat?.Revision ?? 0) != input.ExpectedRevision)
                return Stale();

            var history = chat is null ? [] : await db.Turns.AsNoTracking()
                .Where(turn => turn.ChatId == chat.Id && turn.Revision <= chat.Revision)
                .OrderByDescending(turn => turn.Revision).Take(20).ToListAsync(cancellation);
            history.Reverse();
            string reply;
            try
            {
                reply = await tutor.ReplyAsync(lesson, mode, history, message, cancellation);
            }
            catch (Exception error) when (error is TutorUnavailableException or HttpRequestException or
                System.Text.Json.JsonException or KeyNotFoundException or IOException ||
                error is OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                logger.CreateLogger("Murly.Tutor").LogWarning(error, "Tutor request failed");
                return Results.Problem("The tutor is temporarily unavailable. Try sending again.", statusCode: 503);
            }

            if (chat is null)
            {
                chat = new Chat { LearnerId = learnerId, LessonId = lesson.Id, Mode = mode };
                db.Chats.Add(chat);
            }
            var turn = new Turn
            {
                Revision = ++chat.Revision, RequestId = input.RequestId, Message = message, Reply = reply
            };
            chat.Turns.Add(turn);
            try
            {
                await db.SaveChangesAsync(cancellation);
                return Results.Ok(turn);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var latest = await FindChat(db, learnerId, lesson.Id, mode, cancellation);
                var saved = await FindTurn(db, latest, input.RequestId, cancellation);
                if (saved is not null)
                    return Replay(saved, input, message);
                if ((latest?.Revision ?? 0) != input.ExpectedRevision)
                    return Stale();
                throw;
            }
        }

        private static bool ValidMode(string mode) => mode is "learning" or "practice";

        private static string LearnerKey(ClaimsPrincipal user) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{user.FindFirstValue("iss") ?? throw new InvalidOperationException("Missing issuer.")}\n" +
                $"{user.FindFirstValue("sub") ?? throw new InvalidOperationException("Missing subject.")}")));

        private static Task<Chat?> FindChat(MurlyDb db, string learnerId, string lessonId, string mode,
            CancellationToken cancellation) => db.Chats.SingleOrDefaultAsync(
                chat => chat.LearnerId == learnerId && chat.LessonId == lessonId && chat.Mode == mode, cancellation);

        private static Task<Turn?> FindTurn(MurlyDb db, Chat? chat, Guid requestId, CancellationToken cancellation) =>
            chat is null ? Task.FromResult<Turn?>(null) : db.Turns.AsNoTracking().SingleOrDefaultAsync(
                turn => turn.ChatId == chat.Id && turn.RequestId == requestId, cancellation);

        private static IResult Replay(Turn turn, SendMessage input, string message) =>
            turn.Message == message && turn.Revision - 1 == input.ExpectedRevision
                ? Results.Ok(turn)
                : Results.Problem("This request ID has already been used.", statusCode: 409);

        private static IResult Stale() =>
            Results.Problem("This chat changed in another tab. Review the latest messages before sending.",
                statusCode: 409);
    }
}
