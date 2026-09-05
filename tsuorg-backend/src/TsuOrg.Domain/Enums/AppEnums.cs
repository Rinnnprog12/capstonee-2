namespace TsuOrg.Domain.Enums;

public enum AppRole
{
    OrgOfficer = 1,
    Adviser = 2,
    Dean = 3,
    SouStaff = 4,
    SystemAdmin = 5
}

/// <summary>
/// Typed role within an organization membership (not the app RBAC role).
/// Used to scope Adviser assignments vs officer/member rows.
/// </summary>
public enum MembershipRole
{
    Member = 0,
    Officer = 1,
    Adviser = 2,
}

public enum DocumentStatus
{
    Draft = 0,
    Submitted = 1,
    Validating = 2,
    Flagged = 3,
    UnderReview = 4,
    Returned = 5,
    Approved = 6,
    Rejected = 7,
    Archived = 8
}

public enum WorkflowStage
{
    Officer = 0,
    Calsv = 1,
    Adviser = 2,
    Dean = 3,
    Sou = 4,
    Done = 5
}

public enum ApprovalAction
{
    Approve = 1,
    Reject = 2,
    Return = 3
}

/// <summary>
/// Processing status for individual documents (primary or attachments).
/// Tracks OCR/AI validation progress independently per file.
/// </summary>
public enum ProcessingStatus
{
    Pending = 0,      // Not yet processed
    Queued = 1,       // Waiting in queue
    Processing = 2,   // Currently being OCR'd
    Completed = 3,    // OCR/processing finished successfully
    Failed = 4        // Processing failed (retryable)
}
