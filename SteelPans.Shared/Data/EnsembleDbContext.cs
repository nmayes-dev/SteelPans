using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SteelPans.Shared.Data;

public sealed class EnsembleDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public EnsembleDbContext(DbContextOptions<EnsembleDbContext> options)
        : base(options)
    {
    }

    public DbSet<EnsembleGroup> Groups => Set<EnsembleGroup>();
    public DbSet<EnsembleGroupMember> GroupMembers => Set<EnsembleGroupMember>();
    public DbSet<EnsembleMidiFile> MidiFiles => Set<EnsembleMidiFile>();
    public DbSet<EnsembleGroupMidiFile> GroupMidiFiles => Set<EnsembleGroupMidiFile>();
    public DbSet<EnsembleMidiTrack> MidiTracks => Set<EnsembleMidiTrack>();
    public DbSet<EnsembleMidiTrackAssignment> MidiTrackAssignments => Set<EnsembleMidiTrackAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(x => x.UserName)
                .HasMaxLength(200);
        });

        builder.Entity<EnsembleGroup>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasIndex(x => x.InviteCode)
                .IsUnique();

            entity.Property(x => x.Name)
                .HasMaxLength(200);

            entity.Property(x => x.InviteCode)
                .HasMaxLength(100);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<EnsembleGroupMember>(entity =>
        {
            entity.HasKey(x => new { x.GroupId, x.UserId });

            entity.HasOne(x => x.Group)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(x => x.Role)
                .HasConversion<string>()
                .HasMaxLength(32);
        });

        builder.Entity<EnsembleGroupMidiFile>(entity =>
        {
            entity.HasKey(x => new { x.GroupId, x.MidiFileId });

            entity.HasOne(x => x.Group)
                .WithMany(x => x.SharedMidiFiles)
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.MidiFile)
                .WithMany(x => x.SharedGroups)
                .HasForeignKey(x => x.MidiFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EnsembleMidiFile>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.Title)
                .HasMaxLength(200);

            entity.Property(x => x.OriginalFileName)
                .HasMaxLength(260);

            entity.Property(x => x.StorageKey)
                .HasMaxLength(500);

            entity.Property(x => x.ContentType)
                .HasMaxLength(100);
        });

        builder.Entity<EnsembleMidiTrack>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasIndex(x => new { x.MidiFileId, x.TrackIndex })
                .IsUnique();

            entity.HasOne(x => x.MidiFile)
                .WithMany(x => x.Tracks)
                .HasForeignKey(x => x.MidiFileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(x => x.TrackName)
                .HasMaxLength(200);

            entity.Property(x => x.SuggestedPanType)
                .HasConversion<string?>()
                .HasMaxLength(64);
        });

        builder.Entity<EnsembleMidiTrackAssignment>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.HasIndex(x => new { x.MidiFileId, x.TrackIndex })
                .IsUnique();

            entity.HasOne(x => x.MidiFile)
                .WithMany(x => x.Assignments)
                .HasForeignKey(x => x.MidiFileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(x => x.PanType)
                .HasConversion<string>()
                .HasMaxLength(64);

            entity.Property(x => x.Label)
                .HasMaxLength(200);
        });
    }
}