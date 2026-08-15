namespace BackWave.Pro.Mcp;

// The single source of truth for every MCP tool's wire name. Both the [McpServerTool(Name = ...)]
// attributes on the tool methods and the gate registry in McpToolGates consume these same
// constants, so an attribute and its gate can never desync: renaming a tool changes the one
// constant, and the compiler carries the new value to both the attribute and the gate. (The trap
// this closes: with independent string literals, renaming a write tool's attribute Name would leave
// its gate keyed on the old string, shipping the write tool ungated while every Contains-based test
// still passed.) Values are the wire names — never change one without intending a wire-contract
// change. internal, so the XML-doc gate does not apply.
internal static class ToolNames
{
    // JobTools
    public const string SearchJobs = "search_jobs";
    public const string GetJob = "get_job";
    public const string GetJobHistory = "get_job_history";
    public const string GetJobDependencies = "get_job_dependencies";

    // SensitiveDataTools
    public const string GetJobPayload = "get_job_payload";
    public const string GetJobOutput = "get_job_output";

    // ObserverTools
    public const string GetObserverLag = "get_observer_lag";
    public const string ListObserverDeadLetters = "list_observer_dead_letters";

    // WorkflowTools
    public const string ListWorkflows = "list_workflows";
    public const string GetWorkflow = "get_workflow";
    public const string CancelWorkflow = "cancel_workflow";

    // ReadTools
    public const string GetQueueSettings = "get_queue_settings";
    public const string GetTagFacet = "get_tag_facet";
    public const string ListWireNames = "list_wire_names";
    public const string ListSchedules = "list_schedules";
    public const string ListAuditRecords = "list_audit_records";

    // WriteTools
    public const string CancelJob = "cancel_job";
    public const string RequeueJob = "requeue_job";
    public const string PauseQueue = "pause_queue";
    public const string ResumeQueue = "resume_queue";
    public const string SetConcurrencyLimit = "set_concurrency_limit";
    public const string TriggerSchedule = "trigger_schedule";

    // QueueTools
    public const string GetQueueDepths = "get_queue_depths";
}
