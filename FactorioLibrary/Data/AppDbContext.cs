using FactorioLibrary.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FactorioLibrary.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext(options)
{
    public DbSet<ServerInstance> ServerInstances { get; set; }
    public DbSet<UserApiKey> UserApiKeys { get; set; }
    public DbSet<UserServerAccess> UserServerAccesses { get; set; }
    public DbSet<Modpack> Modpacks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Add model constraints or seed data if needed
    }
}
