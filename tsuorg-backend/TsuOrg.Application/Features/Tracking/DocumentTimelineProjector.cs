using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking;

/// <summary>
/// UI tracker filter tabs (Document Tracker storyboard).
/// Maps to pipeline positions — not raw <see cref="DocumentStatus"/>.
/// </summary>
public enum TrackerTab
{
    All = 0,
    Submitted = 1,
    AiValidated = 2,
    AdviserReview = 3,
    DeanReview = 4,
    Approved = 5,
}

/// <summary>
/// Fixed 5-step DTM pipeline shown on Document Tracker cards.
/// Never skip steps in MVP (CLAUDE.md / WM-01).
/// </summary>
public enum TrackerPipelineStep
{
    Submitted = 0,
    AiValidated = 1,
    AdviserReview = 2,
    DeanReview = 3,
    SouApproved = 4,
}

public sealed record TimelineStepDto(
    string Key,
    string Label,
    string State,
    string Tone);

public sealed record DocumentTimelineView(
    int CurrentIndex,
    string OverallStatus,
    string OverallTone,
    IReadOnlyList<TimelineStepDto> Steps);

/// <summary>
/// Pure projector: DocumentStatus + WorkflowStage → tracker timeline card model.
/// Keep controllers/handlers thin — all timeline rules live here.
/// </summary>
public static class DocumentTimelineProjector
{
    public static readonly IReadOnlyList<(TrackerPipelineStep Step, string Key, string Label)> Pipeline =
    [
        (TrackerPipelineStep.Submitted,     "submitted",  "Submitted"),
        (TrackerPipelineStep.AiValidated,   "ai",         "AI validated"),
        (TrackerPipelineStep.AdviserReview, "adviser",    "Adviser review"),
        (TrackerPipelineStep.DeanReview,    "dean",       "Dean review"),
        (TrackerPipelineStep.SouApproved,   "approved",   "SOU approved"),
    ];

    public static DocumentTimelineView Project(DocumentStatus status, WorkflowStage stage)
    {
        var index = ResolveCurrentIndex(status, stage);
        var tone = ResolveOverallTone(status);
        var overall = ResolveOverallLabel(status, stage);
        var steps = BuildSteps(index, status);

        return new DocumentTimelineView(index, overall, tone, steps);
    }

    /// <summary>0–4 pipeline index matching Document Tracker tabs/timeline.</summary>
    public static int ResolveCurrentIndex(DocumentStatus status, WorkflowStage stage)
    {
        if (status is DocumentStatus.Draft)
            return -1;

        if (status is DocumentStatus.Approved or DocumentStatus.Archived)
            return (int)TrackerPipelineStep.SouApproved;

        if (status is DocumentStatus.Rejected)
            return stage switch
            {
                WorkflowStage.Adviser => (int)TrackerPipelineStep.AdviserReview,
                WorkflowStage.Dean    => (int)TrackerPipelineStep.DeanReview,
                WorkflowStage.Sou or WorkflowStage.Done
                    => (int)TrackerPipelineStep.SouApproved,
                _ => (int)TrackerPipelineStep.AiValidated,
            };

        return status switch
        {
            DocumentStatus.Submitted or DocumentStatus.Validating
                => (int)TrackerPipelineStep.Submitted,

            DocumentStatus.Flagged or DocumentStatus.Returned
                => (int)TrackerPipelineStep.AiValidated,

            DocumentStatus.UnderReview => stage switch
            {
                WorkflowStage.Adviser => (int)TrackerPipelineStep.AdviserReview,
                WorkflowStage.Dean    => (int)TrackerPipelineStep.DeanReview,
                WorkflowStage.Sou     => (int)TrackerPipelineStep.SouApproved,
                WorkflowStage.Calsv   => (int)TrackerPipelineStep.AiValidated,
                _                     => (int)TrackerPipelineStep.AdviserReview,
            },

            _ => (int)TrackerPipelineStep.Submitted,
        };
    }

    public static bool MatchesTab(TrackerTab tab, DocumentStatus status, WorkflowStage stage)
    {
        if (status is DocumentStatus.Draft)
            return false;

        var index = ResolveCurrentIndex(status, stage);

        return tab switch
        {
            TrackerTab.All => true,

            TrackerTab.Submitted =>
                index == (int)TrackerPipelineStep.Submitted
                && status is DocumentStatus.Submitted or DocumentStatus.Validating,

            TrackerTab.AiValidated =>
                index == (int)TrackerPipelineStep.AiValidated
                || status is DocumentStatus.Flagged or DocumentStatus.Returned,

            TrackerTab.AdviserReview =>
                status == DocumentStatus.UnderReview && stage == WorkflowStage.Adviser,

            TrackerTab.DeanReview =>
                status == DocumentStatus.UnderReview && stage == WorkflowStage.Dean,

            TrackerTab.Approved =>
                status is DocumentStatus.Approved or DocumentStatus.Archived
                || (status == DocumentStatus.UnderReview && stage == WorkflowStage.Sou),

            _ => true,
        };
    }

    private static IReadOnlyList<TimelineStepDto> BuildSteps(int currentIndex, DocumentStatus status)
    {
        var steps = new List<TimelineStepDto>(Pipeline.Count);

        for (var i = 0; i < Pipeline.Count; i++)
        {
            var (_, key, label) = Pipeline[i];
            var (state, tone) = StepState(i, currentIndex, status);
            steps.Add(new TimelineStepDto(key, label, state, tone));
        }

        return steps;
    }

    private static (string State, string Tone) StepState(int stepIndex, int currentIndex, DocumentStatus status)
    {
        if (currentIndex < 0)
            return ("Pending", "upcoming");

        if (stepIndex < currentIndex)
            return ("Done", "completed");

        if (stepIndex > currentIndex)
            return ("Pending", "upcoming");

        // Active step
        if (status is DocumentStatus.Rejected)
            return ("Rejected", "upcoming");

        if (status is DocumentStatus.Flagged)
            return ("Flagged", "upcoming");

        if (status is DocumentStatus.Returned)
            return ("Returned", "upcoming");

        if (status is DocumentStatus.Approved or DocumentStatus.Archived)
            return ("Done", "completed");

        if (status is DocumentStatus.Validating or DocumentStatus.Submitted)
            return ("In progress", "progress");

        return ("In progress", "progress");
    }

    private static string ResolveOverallLabel(DocumentStatus status, WorkflowStage stage) =>
        status switch
        {
            DocumentStatus.Draft       => "Draft",
            DocumentStatus.Submitted   => "Submitted",
            DocumentStatus.Validating  => "AI validating",
            DocumentStatus.Flagged     => "Flagged — needs revision",
            DocumentStatus.Returned    => "Returned for revision",
            DocumentStatus.UnderReview => stage switch
            {
                WorkflowStage.Adviser => "Adviser review",
                WorkflowStage.Dean    => "Dean review",
                WorkflowStage.Sou     => "SOU review",
                _                     => "Under review",
            },
            DocumentStatus.Approved  => "Approved",
            DocumentStatus.Rejected  => "Rejected",
            DocumentStatus.Archived  => "Archived",
            _                        => status.ToString(),
        };

    private static string ResolveOverallTone(DocumentStatus status) =>
        status switch
        {
            DocumentStatus.Approved or DocumentStatus.Archived => "completed",
            DocumentStatus.Rejected or DocumentStatus.Flagged or DocumentStatus.Returned => "upcoming",
            DocumentStatus.UnderReview or DocumentStatus.Validating or DocumentStatus.Submitted => "progress",
            _ => "upcoming",
        };
}
