using IamOrchestrator.Models;
using NCrontab;
using System.Collections.Concurrent;

namespace IamOrchestrator.Services;

public sealed class JobScheduler : IJobScheduler
{
    private readonly ILogger<JobScheduler> _logger;
    private readonly ConcurrentDictionary<Guid, ScheduledJobInfo> _scheduledJobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellations = new();

    public JobScheduler(ILogger<JobScheduler> logger)
    {
        _logger = logger;
    }

    public void ScheduleJob(Job job, Func<Task> executeCallback)
    {
        if (string.IsNullOrEmpty(job.Schedule))
        {
            _logger.LogWarning("Cannot schedule job {JobId} - no schedule defined", job.Id);
            return;
        }

        UnscheduleJob(job.Id); // Remove existing schedule if any

        try
        {
            var schedule = CrontabSchedule.Parse(job.Schedule);
            var cts = new CancellationTokenSource();
            
            var scheduledJob = new ScheduledJobInfo
            {
                Job = job,
                Schedule = schedule,
                NextRun = schedule.GetNextOccurrence(DateTime.UtcNow)
            };

            _scheduledJobs[job.Id] = scheduledJob;
            _jobCancellations[job.Id] = cts;

            // Start background task to monitor and execute
            _ = Task.Run(async () => await MonitorScheduledJobAsync(job.Id, executeCallback, cts.Token), cts.Token);

            _logger.LogInformation("Scheduled job {JobId} ({JobName}) - Next run: {NextRun}", 
                job.Id, job.Name, scheduledJob.NextRun);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule job {JobId} with schedule {Schedule}", job.Id, job.Schedule);
        }
    }

    public void UnscheduleJob(Guid jobId)
    {
        if (_jobCancellations.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_scheduledJobs.TryRemove(jobId, out var scheduledJob))
        {
            _logger.LogInformation("Unscheduled job {JobId} ({JobName})", jobId, scheduledJob.Job.Name);
        }
    }

    public bool IsJobScheduled(Guid jobId)
    {
        return _scheduledJobs.ContainsKey(jobId);
    }

    private async Task MonitorScheduledJobAsync(Guid jobId, Func<Task> executeCallback, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_scheduledJobs.TryGetValue(jobId, out var scheduledJob))
                {
                    break;
                }

                var now = DateTime.UtcNow;
                var timeUntilNext = scheduledJob.NextRun - now;

                if (timeUntilNext.TotalSeconds <= 0)
                {
                    // Time to execute
                    if (!scheduledJob.Job.IsPaused)
                    {
                        _logger.LogInformation("Executing scheduled job {JobId} ({JobName})", 
                            scheduledJob.Job.Id, scheduledJob.Job.Name);
                        
                        try
                        {
                            await executeCallback();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error executing scheduled job {JobId}", jobId);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Skipping paused scheduled job {JobId} ({JobName})", 
                            scheduledJob.Job.Id, scheduledJob.Job.Name);
                    }

                    // Calculate next run
                    scheduledJob.NextRun = scheduledJob.Schedule.GetNextOccurrence(DateTime.UtcNow);
                    _logger.LogInformation("Next run for job {JobId} scheduled at {NextRun}", 
                        jobId, scheduledJob.NextRun);
                }
                else
                {
                    // Wait until next execution (or max 1 minute to check for updates)
                    var waitTime = timeUntilNext > TimeSpan.FromMinutes(1) 
                        ? TimeSpan.FromMinutes(1) 
                        : timeUntilNext;
                    
                    await Task.Delay(waitTime, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled job monitor for {JobId}", jobId);
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }

        _logger.LogInformation("Stopped monitoring scheduled job {JobId}", jobId);
    }

    private class ScheduledJobInfo
    {
        public Job Job { get; set; } = null!;
        public CrontabSchedule Schedule { get; set; } = null!;
        public DateTime NextRun { get; set; }
    }
}
