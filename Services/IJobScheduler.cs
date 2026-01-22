using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public interface IJobScheduler
{
    void ScheduleJob(Job job, Func<Task> executeCallback);
    void UnscheduleJob(Guid jobId);
    bool IsJobScheduled(Guid jobId);
}
