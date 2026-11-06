using System.ComponentModel.DataAnnotations;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

public class WorkflowHubOptions
{
    public const string SectionName = "WorkflowHub";

    /// <summary>
    /// How long the hook request waits for crate preparation before it records a failure
    /// and answers with the preview link anyway.
    /// </summary>
    [Range(1, 3600)]
    public int PreparationTimeoutSeconds { get; set; } = 180;
}
