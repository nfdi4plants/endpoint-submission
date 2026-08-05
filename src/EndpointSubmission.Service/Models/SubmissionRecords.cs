namespace EndpointSubmission.Service.Models;

/// <summary>
/// One transaction with the endpoint repository. A resubmission is a new attempt against
/// the same <see cref="Submission"/>, never a new submission — otherwise the history of
/// what was tried against a given ARC is lost.
/// </summary>
public class SubmissionAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubmissionId { get; set; }
    public Submission? Submission { get; set; }

    public int AttemptNumber { get; set; }
    public SubmissionStatus Status { get; set; } = SubmissionStatus.Submitting;

    /// <summary>
    /// ENA's own submission id where the async submit/poll pair is used. Mirrored rather
    /// than reinvented — ENA already models long-running submission.
    /// </summary>
    public string? ExternalSubmissionId { get; set; }

    public string? ErrorSummary { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public List<SubmissionPayload> Payloads { get; set; } = [];
    public List<SubmissionReceipt> Receipts { get; set; } = [];
}

/// <summary>
/// The exact document sent to the endpoint repository. Non-negotiable for a system whose
/// actions mint permanent public identifiers: without it there is no way to answer
/// "what did we actually submit?" after the fact.
/// </summary>
public class SubmissionPayload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttemptId { get; set; }
    public SubmissionAttempt? Attempt { get; set; }

    /// <summary>SUBMISSION, STUDY, SAMPLE, EXPERIMENT, RUN.</summary>
    public string ObjectKind { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SubmissionReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttemptId { get; set; }
    public SubmissionAttempt? Attempt { get; set; }

    /// <summary>Raw receipt, unparsed. Parsed values live in <see cref="Accession"/>.</summary>
    public string Content { get; set; } = string.Empty;

    public bool Success { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A permanent public identifier minted by the endpoint repository. Displayed, not written
/// back to the ARC — see plans/0001.
/// </summary>
public class Accession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubmissionId { get; set; }
    public Submission? Submission { get; set; }

    public string ObjectKind { get; set; } = string.Empty;

    /// <summary>The alias we submitted under. Deterministic, so resubmission targets the same object.</summary>
    public string Alias { get; set; } = string.Empty;

    public string AccessionNumber { get; set; } = string.Empty;
    public string? SecondaryAccession { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum SubmissionEventLevel
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>
/// Append-only step log. This is what the status page renders, and — since there is no
/// write-back to the ARC — a large part of the only record that a submission happened.
/// </summary>
public class SubmissionEvent
{
    public long Id { get; set; }
    public Guid SubmissionId { get; set; }
    public Submission? Submission { get; set; }

    public string Step { get; set; } = string.Empty;
    public SubmissionEventLevel Level { get; set; } = SubmissionEventLevel.Info;
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubmissionId { get; set; }

    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public bool Sent { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
}
