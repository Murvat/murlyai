using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Murly;

public sealed class DesignTimeDatabase : IDesignTimeDbContextFactory<MurlyDb>
{
    public MurlyDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MurlyDb>()
        .UseSqlServer("Server=localhost;Database=Murly;Integrated Security=True;TrustServerCertificate=True")
        .Options);
}
