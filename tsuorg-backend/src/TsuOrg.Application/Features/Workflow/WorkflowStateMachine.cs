using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow;

/// <summary>
/// WM-01: Sequential pipeline Officer → Adviser → Dean → SOU.
/// Encodes legal stage transitions for Approve / Reject / Return.
/// </summary>
public static class WorkflowStateMachine
{
    /// <summary>Role that may act at the given stage.</summary>
    public static AppRole RequiredRole(WorkflowStage stage) => stage switch
    {
        WorkflowStage.Adviser => AppRole.Adviser,
        WorkflowStage.Dean    => AppRole.Dean,
        WorkflowStage.Sou     => AppRole.SouStaff,
        _ => throw new InvalidOperationException($"Stage '{stage}' is not a review stage.")
    };

    public static bool CanAct(AppRole actorRole, WorkflowStage currentStage)
    {
        if (actorRole == AppRole.SystemAdmin)
            return currentStage is WorkflowStage.Adviser or WorkflowStage.Dean or WorkflowStage.Sou;

        return actorRole == RequiredRole(currentStage);
    }

    /// <summary>
    /// Apply Approve: advance to next stage, or complete at SOU.
    /// </summary>
    public static (DocumentStatus Status, WorkflowStage Stage, bool WorkflowComplete) ApplyApprove(
        WorkflowStage currentStage)
    {
        return currentStage switch
        {
            WorkflowStage.Adviser => (DocumentStatus.UnderReview, WorkflowStage.Dean, false),
            WorkflowStage.Dean    => (DocumentStatus.UnderReview, WorkflowStage.Sou,  false),
            WorkflowStage.Sou     => (DocumentStatus.Approved,    WorkflowStage.Done, true),
            _ => throw new InvalidOperationException($"Cannot Approve at stage '{currentStage}'.")
        };
    }

    /// <summary>Reject ends the workflow permanently.</summary>
    public static (DocumentStatus Status, WorkflowStage Stage, bool WorkflowComplete) ApplyReject()
        => (DocumentStatus.Rejected, WorkflowStage.Done, true);

    /// <summary>Return for revision sends package back to the officer.</summary>
    public static (DocumentStatus Status, WorkflowStage Stage, bool WorkflowComplete) ApplyReturn()
        => (DocumentStatus.Returned, WorkflowStage.Officer, true);
}
