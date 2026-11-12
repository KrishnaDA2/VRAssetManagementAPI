
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Domain.Entities;

namespace VRAssetManagement.Infrastructure.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();
    public DbSet<AssetFile> Files => Set<AssetFile>();
    public DbSet<MaterialMapping> MaterialMappings => Set<MaterialMapping>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DownloadLog> DownloadLogs => Set<DownloadLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Company
        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200).IsRequired();

            entity.Property(x => x.Slug)
                .HasMaxLength(200).IsRequired();

            entity.HasIndex(x => x.Slug).IsUnique();

            entity.HasMany(x => x.Projects)
                .WithOne(x => x.Company)
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Project
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200).IsRequired();

            entity.Property(x => x.Slug)
                .HasMaxLength(200).IsRequired();

            entity.HasIndex(x => new { x.CompanyId, x.Slug })
                .IsUnique();

            entity.Property(x => x.Status)
                .HasMaxLength(50).IsRequired();
        });

        // User
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200).IsRequired();

            entity.Property(x => x.Email)
                .HasMaxLength(320).IsRequired();

            entity.Property(x => x.GlobalRole)
                .HasMaxLength(50).IsRequired();

            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasIndex(x => x.GoogleId)
                .IsUnique()
                .HasFilter("[GoogleId] IS NOT NULL");
        });

        // Project membership: composite primary key
        modelBuilder.Entity<ProjectMember>(entity =>
        {
            entity.HasKey(x => new { x.ProjectId, x.UserId });

            entity.Property(x => x.Role)
                .HasMaxLength(50).IsRequired();

            entity.HasOne(x => x.Project)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.User)
                .WithMany(x => x.Memberships)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Model
        modelBuilder.Entity<Model>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200).IsRequired();

            entity.Property(x => x.Slug)
                .HasMaxLength(200).IsRequired();

            entity.Property(x => x.Category)
                .HasMaxLength(50).IsRequired();

            entity.HasIndex(x => new { x.ProjectId, x.Slug })
                .IsUnique();

            entity.HasOne(x => x.Project)
                .WithMany(x => x.Models)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<ModelVersion>()
                .WithMany()
                .HasForeignKey(x => x.LatestVersionId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne<ModelVersion>()
                .WithMany()
                .HasForeignKey(x => x.LatestApprovedVersionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // Model versions
        modelBuilder.Entity<ModelVersion>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Status)
                .HasMaxLength(50).IsRequired();

            entity.Property(x => x.ProcessingStatus)
                .HasMaxLength(50).IsRequired();

            entity.Property(x => x.StatsJson)
                .HasColumnType("nvarchar(max)");

            entity.HasIndex(x => new { x.ModelId, x.VersionNo })
                .IsUnique();

            entity.HasOne(x => x.Model)
                .WithMany(x => x.Versions)
                .HasForeignKey(x => x.ModelId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(x => x.UploadedBy)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Asset files
        modelBuilder.Entity<AssetFile>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Kind)
                .HasMaxLength(50).IsRequired();

            entity.Property(x => x.Filename)
                .HasMaxLength(500).IsRequired();

            entity.HasOne(x => x.Version)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Material mappings
        modelBuilder.Entity<MaterialMapping>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.MaterialName)
                .HasMaxLength(200).IsRequired();

            entity.Property(x => x.Slot)
                .HasMaxLength(50).IsRequired();

            entity.Property(x => x.ColorSpace)
                .HasMaxLength(20).IsRequired();

            entity.HasOne(x => x.Version)
                .WithMany(x => x.MaterialMappings)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.File)
                .WithMany()
                .HasForeignKey(x => x.FileId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // Comments
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Body)
                .HasMaxLength(5000).IsRequired();

            entity.HasOne(x => x.Version)
                .WithMany(x => x.Comments)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Audit logs
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Action)
                .HasMaxLength(100).IsRequired();

            entity.Property(x => x.TargetType)
                .HasMaxLength(100).IsRequired();

            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => x.At);
        });

        // Download logs
        modelBuilder.Entity<DownloadLog>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<AssetFile>()
                .WithMany()
                .HasForeignKey(x => x.FileId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => x.At);
        });
        // User
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.Email)
                .HasMaxLength(320)
                .IsRequired();

            entity.Property(x => x.GlobalRole)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(x => x.PermissionsJson)
                .HasColumnType("nvarchar(max)")
                .IsRequired()
                .HasDefaultValue("[]");

            entity.HasIndex(x => x.Email).IsUnique();

            entity.HasIndex(x => x.GoogleId)
                .IsUnique()
                .HasFilter("[GoogleId] IS NOT NULL");
        });
    }
}
