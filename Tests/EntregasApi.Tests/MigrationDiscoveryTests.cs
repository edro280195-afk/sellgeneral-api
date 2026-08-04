using EntregasApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EntregasApi.Tests;

public class MigrationDiscoveryTests
{
    [Fact]
    public void MigrationsAssembly_IncludesBusinessOrderNumberMigration()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=discovery_test;Username=test;Password=test")
            .Options;

        using var db = new AppDbContext(options);
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();

        Assert.Contains(
            "20260803182000_AddBusinessOrderNumber",
            migrationsAssembly.Migrations.Keys);
    }
}
