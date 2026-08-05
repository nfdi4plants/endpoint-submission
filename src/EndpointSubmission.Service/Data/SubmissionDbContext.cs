using EndpointSubmission.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace EndpointSubmission.Service.Data;

public class SubmissionDbContext(DbContextOptions<SubmissionDbContext> options) : DbContext(options)
{
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<SubmissionAttempt> SubmissionAttempts => Set<SubmissionAttempt>();
    public DbSet<SubmissionPayload> SubmissionPayloads => Set<SubmissionPayload>();
    public DbSet<SubmissionReceipt> SubmissionReceipts => Set<SubmissionReceipt>();
    public DbSet<Accession> Accessions => Set<Accession>();
    public DbSet<SubmissionEvent> SubmissionEvents => Set<SubmissionEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Submission>(e =>
        {
            e.HasKey(x => x.Id);

            // Idempotency: a badge is cheap to click twice. One submission per ARC commit
            // per endpoint per target, enforced by the database rather than by a race-prone
            // read-then-write in the handler.
            e.HasIndex(x => new { x.DataHubHost, x.ProjectId, x.CommitSha, x.Endpoint, x.Target }).IsUnique();

            // The status page looks submissions up by token hash on every request.
            e.HasIndex(x => x.StatusTokenHash);

            e.Property(x => x.ProjectName).HasMaxLength(512).IsRequired();
            e.Property(x => x.DataHubHost).HasMaxLength(256).IsRequired();
            e.Property(x => x.CommitSha).HasMaxLength(64).IsRequired();
            e.Property(x => x.RoCrateUrl).HasMaxLength(2048).IsRequired();
            e.Property(x => x.RoCrateJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(64).IsRequired();
            e.Property(x => x.SubmitterEmail).HasMaxLength(512);
            e.Property(x => x.SubmitterGitLabUser).HasMaxLength(256);
            e.Property(x => x.StatusTokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.PackagePayloadJson).HasColumnType("jsonb");

            e.HasMany(x => x.Attempts).WithOne(x => x.Submission)
                .HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Accessions).WithOne(x => x.Submission)
                .HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne(x => x.Submission)
                .HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SubmissionAttempt>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SubmissionId, x.AttemptNumber }).IsUnique();
            e.Property(x => x.ExternalSubmissionId).HasMaxLength(256);

            e.HasMany(x => x.Payloads).WithOne(x => x.Attempt)
                .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Receipts).WithOne(x => x.Attempt)
                .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SubmissionPayload>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ObjectKind).HasMaxLength(64).IsRequired();
        });

        b.Entity<SubmissionReceipt>(e => e.HasKey(x => x.Id));

        b.Entity<Accession>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ObjectKind).HasMaxLength(64).IsRequired();
            e.Property(x => x.Alias).HasMaxLength(512).IsRequired();
            e.Property(x => x.AccessionNumber).HasMaxLength(128).IsRequired();
            e.Property(x => x.SecondaryAccession).HasMaxLength(128);
        });

        b.Entity<SubmissionEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SubmissionId, x.OccurredAt });
            e.Property(x => x.Step).HasMaxLength(128).IsRequired();
        });

        b.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Recipient).HasMaxLength(512).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(512).IsRequired();
        });
    }
}
