# IAM Orchestrator

A .NET 10 Worker Service that orchestrates job execution by polling the IAM API and executing Docker containers.

## Features

- **Job Polling** - Continuously polls the IAM API for pending jobs
- **One-Off Jobs** - Executes jobs immediately upon detection
- **Scheduled Jobs** - Uses cron expressions to schedule recurring jobs
- **Container Execution** - Executes jobs in Docker containers with parameters
- **Log Streaming** - Streams container logs back to the API in real-time
- **Job Status Management** - Updates job status (Pending → Running → Completed/Failed)
- **Pause/Resume** - Respects pause state for scheduled jobs

## Configuration

Edit `appsettings.json`:

```json
{
  "ApiSettings": {
    "BaseUrl": "http://localhost:5000",
    "PollingIntervalSeconds": 10
  },
  "ContainerSettings": {
    "DefaultTimeout": 3600,
    "DockerEndpoint": "npipe://./pipe/docker_engine"
  },
  "CustomerSettings": {
    "CustomerName": "alpha"
  }
}
```

### Customer Configuration

Set `CustomerSettings:CustomerName` to the specific customer this orchestrator instance should handle. If left empty, the orchestrator will process jobs for **all customers**.

**Example configurations:**
- For customer "alpha": `"CustomerName": "alpha"`
- For customer "bravo": `"CustomerName": "bravo"`
- For all customers: `"CustomerName": ""`

### Docker Endpoint

- **Windows**: `npipe://./pipe/docker_engine`
- **Linux**: `unix:///var/run/docker.sock`

## Running Locally

```powershell
dotnet run
```

## Building Docker Image

```powershell
docker build -t iam-orchestrator .
```

## Running in Docker

### Windows (with Docker Desktop)

```powershell
docker run -d \
  -v \\.\pipe\docker_engine:\\.\pipe\docker_engine \
  -e ApiSettings__BaseUrl=http://host.docker.internal:5000 \
  -e CustomerSettings__CustomerName=alpha \
  iam-orchestrator
```

### Linux

```bash
docker run -d \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e ApiSettings__BaseUrl=http://host.docker.internal:5000 \
  -e CustomerSettings__CustomerName=alpha \
  iam-orchestrator
```

## Multi-Customer Deployment

You can run multiple orchestrator instances, one per customer:

```powershell
# Orchestrator for customer "alpha"
docker run -d --name iam-orchestrator-alpha \
  -v \\.\pipe\docker_engine:\\.\pipe\docker_engine \
  -e ApiSettings__BaseUrl=http://host.docker.internal:5000 \
  -e CustomerSettings__CustomerName=alpha \
  iam-orchestrator

# Orchestrator for customer "bravo"
docker run -d --name iam-orchestrator-bravo \
  -v \\.\pipe\docker_engine:\\.\pipe\docker_engine \
  -e ApiSettings__BaseUrl=http://host.docker.internal:5000 \
  -e CustomerSettings__CustomerName=bravo \
  iam-orchestrator
```

## How It Works

1. **Polling**: Every 10 seconds (configurable), polls `/api/jobs/pending`
2. **Job Classification**:
   - **One-Off Jobs**: Executed immediately once
   - **Scheduled Jobs**: Scheduled using cron expression
3. **Execution**:
   - Updates job status to `Running`
   - Pulls Docker image if needed
   - Creates and starts container with job parameters as environment variables
   - Streams container logs to API
   - Updates job status to `Completed` or `Failed`
4. **Scheduled Jobs**:
   - Monitors cron schedule
   - Executes at scheduled times
   - Skips execution if job is paused

## Environment Variables Passed to Containers

The orchestrator passes these environment variables to job containers:

- `JOB_ID` - Unique job identifier
- `JOB_NAME` - Job name
- `CUSTOMER` - Customer name
- `SCRIPT_PATH` - Path to the script to execute
- `WHAT_IF` - Boolean indicating dry-run mode
- All job parameters as `KEY=value`

## Cron Expression Examples

- `0 0 * * *` - Daily at midnight
- `0 */6 * * *` - Every 6 hours
- `30 2 * * 1` - Every Monday at 2:30 AM
- `0 9 * * 1-5` - Weekdays at 9:00 AM

## Dependencies

- **NCrontab** - Cron expression parsing and scheduling
- **Docker.DotNet** - Docker API client for container management
- **Microsoft.Extensions.Hosting** - Background service framework
# iam-orchestrator
