using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Maia.Infrastructure.DataAccess;

/// <summary>Used by 'dotnet ef migrations' at design time only.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MaiaDbContext>
{
    public MaiaDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=AIEngineDb;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new MaiaDbContext(opts);
    }
}
