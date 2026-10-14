namespace BlackFriday.Api.Services;

public class TenantPipelineMetrics
{
    public long EnqueuedCount;
    public long ProcessedCount;
    public long RetryCount;
    public long DlqCount;
}