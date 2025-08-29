using Lango.Api;
using Microsoft.EntityFrameworkCore;

namespace Lango.Api.Data;

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<Word> Words => Set<Word>();
    public DbSet<Lookup> Lookups => Set<Lookup>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Sentence> Sentences => Set<Sentence>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Word>().HasIndex(x => x.Lemma).IsUnique();
        b.Entity<Review>().HasIndex(x => new { x.UserId, x.WordId }).IsUnique();

        b.Entity<Word>().Property(x => x.DefsJson).HasColumnType("jsonb");
        b.Entity<Word>().Property(x => x.PhoneticsJson).HasColumnType("jsonb");
    }
}
