using Microsoft.EntityFrameworkCore;
using System.Reflection.Emit;

namespace SteelPans.EnsembleService.Data;

public sealed class EnsembleDbContext(DbContextOptions<EnsembleDbContext> options)
    : DbContext(options)
{
    public DbSet<EnsembleUser> Users => Set<EnsembleUser>();
    public DbSet<EnsembleGroup> Groups => Set<EnsembleGroup>();
    public DbSet<EnsembleGroupMember> GroupMembers => Set<EnsembleGroupMember>();
    public DbSet<EnsembleMidiFile> MidiFiles => Set<EnsembleMidiFile>();
    public DbSet<EnsembleMidiTrack> MidiTracks => Set<EnsembleMidiTrack>();
    public DbSet<EnsembleMidiTrackAssignment> MidiTrackAssignments => Set<EnsembleMidiTrackAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<EnsembleUser>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
        });

        builder.Entity<EnsembleGroup>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Slug).HasMaxLength(100);
        });

        builder.Entity<EnsembleGroupMember>(entity =>
        {
            entity.HasKey(x => new { x.GroupId, x.UserId });

            entity.HasOne(x => x.Group)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.GroupId);

            entity.HasOne(x => x.User)
                .WithMany(x => x.Memberships)
                .HasForeignKey(x => x.UserId);

            entity.Property(x => x.Role)
                .HasConversion<string>()
                .HasMaxLength(32);
        });

        builder.Entity<EnsembleMidiFile>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.Group)
                .WithMany(x => x.MidiFiles)
                .HasForeignKey(x => x.GroupId);

            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.OriginalFileName).HasMaxLength(260);
            entity.Property(x => x.StorageKey).HasMaxLength(500);
            entity.Property(x => x.ContentType).HasMaxLength(100);
        });

        builder.Entity<EnsembleMidiTrack>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasIndex(x => new { x.MidiFileId, x.TrackIndex })
                .IsUnique();

            entity.Property(x => x.SuggestedPanType)
                .HasConversion<string?>()
                .HasMaxLength(64);
        });

        builder.Entity<EnsembleMidiTrackAssignment>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasIndex(x => new { x.MidiFileId, x.TrackIndex })
                .IsUnique();

            entity.Property(x => x.PanType)
                .HasConversion<string>()
                .HasMaxLength(64);
        });
    }
}