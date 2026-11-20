namespace VisualCronAI.Mcp;

internal static class ServerInstructions
{
    public const string Text = """
        VisualCron job management (real VisualCron servers, environments such as DEV/UAT/PROD).

        Workflow for ANY change (enforced by the server, not optional):
        1. Prepare: visualcron_draft_job + visualcron_create_job, or visualcron_clone_job / visualcron_update_job /
           visualcron_add_task / visualcron_update_task / visualcron_remove_task / visualcron_update_schedule /
           visualcron_enable_job / visualcron_disable_job. These only PREPARE a proposal; nothing is saved.
        2. Show the user the proposal's summary, changes, validation warnings and next run times
           (visualcron_preview_changes for full detail). Fix validation errors by preparing a new proposal.
        3. visualcron_request_approval: the server asks the HUMAN directly (approval form or Windows dialog).
           You cannot approve on the user's behalf. Production needs an extra typed confirmation.
        4. visualcron_apply_approved_changes: writes to VisualCron, verifies by reading the job back, rolls back on failure.

        ADF: jobs run Azure Data Factory pipelines through VisualCronAI.AdfRunner.exe (task type "AdfPipeline"); it starts
        the pipeline, logs the run ID, waits for completion and fails the task if the pipeline fails. Retries are task
        flows; failure e-mails are existing VisualCron notifications (visualcron_list_notifications) attached to the job.
        Never put passwords, keys or tokens in job definitions or parameters; the server rejects them.
        Times: give a time zone (e.g. "Central Standard Time" or "CT"); times are converted to the server's time zone.
        Jobs can be referenced by exact name or ID; tasks by name, ID, 1-based position or pipeline name.
        """;
}
