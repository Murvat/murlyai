using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Murly;

public sealed record LessonExample(string Phrase, string Translation);

public sealed class Lesson
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string TargetLanguage { get; set; }
    public int DisplayOrder { get; set; }
    public required string OpeningContent { get; set; }
    public List<LessonExample> Examples { get; set; } = [];
}

public sealed class Chat
{
    public int Id { get; set; }
    public required string LearnerId { get; set; }
    public required string LessonId { get; set; }
    public required string Mode { get; set; }
    public int Revision { get; set; }
    public List<Turn> Turns { get; set; } = [];
}

// Each row is one complete exchange, with a stable position and a client request ID.
public sealed class Turn
{
    public int ChatId { get; set; }
    public int Revision { get; set; }
    public Guid RequestId { get; set; }
    public required string Message { get; set; }
    public required string Reply { get; set; }
}

public sealed class MurlyDb(DbContextOptions<MurlyDb> options) : DbContext(options)
{
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<Turn> Turns => Set<Turn>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Lesson>(lesson =>
        {
            lesson.Property(x => x.Id).HasMaxLength(80);
            lesson.Property(x => x.Title).HasMaxLength(160);
            lesson.Property(x => x.TargetLanguage).HasMaxLength(80);
            lesson.OwnsMany(x => x.Examples, examples => examples.ToJson());
        });
        model.Entity<Chat>(chat =>
        {
            chat.Property(x => x.LearnerId).HasMaxLength(64);
            chat.Property(x => x.LessonId).HasMaxLength(80);
            chat.Property(x => x.Mode).HasMaxLength(8);
            chat.Property(x => x.Revision).IsConcurrencyToken();
            chat.HasIndex(x => new { x.LearnerId, x.LessonId, x.Mode }).IsUnique();
            chat.HasOne<Lesson>().WithMany().HasForeignKey(x => x.LessonId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Turn>(turn =>
        {
            turn.HasKey(x => new { x.ChatId, x.Revision });
            turn.HasIndex(x => new { x.ChatId, x.RequestId }).IsUnique();
            turn.Property(x => x.Message).HasMaxLength(4000);
        });
    }
}

public static partial class Curriculum
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex LessonIdPattern();

    public static async Task<int> ImportAsync(MurlyDb db, string path, CancellationToken cancellation)
    {
        await using var file = File.OpenRead(path);
        var lessons = await JsonSerializer.DeserializeAsync<List<Lesson>>(file, Json, cancellation);
        if (lessons is null)
            throw new InvalidDataException("Curriculum must be a JSON array.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lesson in lessons)
        {
            if (lesson is null ||
                !ValidText(lesson.Id, 80) || !LessonIdPattern().IsMatch(lesson.Id) || !ids.Add(lesson.Id) ||
                !ValidText(lesson.Title, 160) || !ValidText(lesson.TargetLanguage, 80) ||
                !ValidText(lesson.OpeningContent, 12000) || lesson.DisplayOrder < 0 ||
                lesson.Examples is null || lesson.Examples.Count > 20 ||
                lesson.Examples.Any(example => example is null ||
                    !ValidText(example.Phrase, 500) || !ValidText(example.Translation, 500)))
                throw new InvalidDataException($"Invalid or duplicate lesson: {lesson?.Id ?? "(missing ID)"}.");
        }

        foreach (var lesson in lessons)
        {
            var existing = await db.Lessons.FindAsync([lesson.Id], cancellation);
            if (existing is null)
            {
                db.Lessons.Add(lesson);
                continue;
            }

            existing.Title = lesson.Title;
            existing.TargetLanguage = lesson.TargetLanguage;
            existing.DisplayOrder = lesson.DisplayOrder;
            existing.OpeningContent = lesson.OpeningContent;
            existing.Examples = lesson.Examples;
        }

        await db.SaveChangesAsync(cancellation);
        return lessons.Count;
    }

    private static bool ValidText(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit;
}
