using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// One hundred and fifty fictional jobs of a cleaning company in every status (SPEC 8.3), spread from four months
/// ago to a month ahead in the organization's time zone, over its active clients and its working members. Built
/// through the domain like real use would, so numbers, the workflow and the audit history are genuine. Deterministic.
/// Old jobs are mostly Completed, a few stayed open past their due date (overdue), recent ones are in progress and
/// upcoming ones New or Scheduled.
/// </summary>
internal static class DemoJobs
{
    public const int Count = 150;

    private static readonly string[] Tasks =
    [
        "Weekly office clean", "Deep clean of the kitchen area", "Window cleaning, all floors", "Carpet shampoo",
        "Post-renovation clean", "Floor polishing", "Sanitize touchpoints and restrooms", "Restock supplies and paper goods",
        "Move-out clean", "Reception and lobby refresh", "Upholstery steam clean", "Waste room and bins deep clean",
    ];

    private static readonly string[] CancelReasons =
    [
        "Client postponed the contract", "Duplicate of another booking", "Building closed for repairs", "Client chose another date",
    ];

    public static IEnumerable<Job> Create(
        Organization organization,
        IReadOnlyList<Client> activeClients,
        IReadOnlyList<(Guid UserId, string Name)> workers,
        Guid managerId,
        IReadOnlyDictionary<Guid, string> names,
        DateTimeOffset now)
    {
        var manager = JobActor.From(managerId, Role.Owner);
        var today = organization.TodayAt(now);
        var priorities = new[] { JobPriority.Normal, JobPriority.Low, JobPriority.Normal, JobPriority.High, JobPriority.Urgent, JobPriority.Normal };

        for (var i = 0; i < Count; i++)
        {
            var offset = -120 + i; // days from today: the first job is four months back, the last a month ahead
            var scheduled = today.AddDays(offset);
            var target = TargetStatus(i, offset);
            var client = activeClients[i % activeClients.Count];
            var task = Tasks[i % Tasks.Length];

            // About one in seven New jobs is still unassigned, so "unassigned" has something to find.
            Guid? assignee = target == JobStatus.New && i % 7 == 0 ? null : workers[i % workers.Count].UserId;
            var details = JobDetails.Validate(
                $"{task} – {client.Name}",
                i % 3 == 0 ? $"Access through the main entrance. Contact: {client.ContactName ?? "front desk"}." : null,
                client.Id,
                assignee,
                priorities[i % priorities.Length],
                target == JobStatus.New ? null : scheduled,
                i % 9 == 0 ? null : scheduled.AddDays((i % 5) + 1),
                i % 6 == 0 ? null : 80m + (i * 37 % 40) * 10m);

            var job = Job.Create(organization.Id, organization.TakeJobNumber(), details, manager, names, now);
            Advance(job, target, manager, i, now);
            yield return job;
        }
    }

    private static JobStatus TargetStatus(int i, int offset)
    {
        if (offset > 0)
        {
            return i % 2 == 0 ? JobStatus.New : JobStatus.Scheduled;
        }

        if (offset > -30)
        {
            return (i % 10) switch
            {
                0 or 1 or 2 or 3 => JobStatus.Completed,
                4 or 5 or 6 => JobStatus.InProgress,
                7 => JobStatus.Scheduled,
                8 => JobStatus.New,
                _ => JobStatus.Cancelled,
            };
        }

        // Older work is done, with a few cancellations and a few that were never closed (overdue).
        return (i % 25) switch
        {
            0 => JobStatus.InProgress,
            12 => JobStatus.Scheduled,
            _ when i % 10 == 9 => JobStatus.Cancelled,
            _ => JobStatus.Completed,
        };
    }

    private static void Advance(Job job, JobStatus target, JobActor manager, int i, DateTimeOffset now)
    {
        switch (target)
        {
            case JobStatus.Scheduled:
                job.TransitionTo(JobStatus.Scheduled, manager, null, now);
                break;
            case JobStatus.InProgress:
                job.TransitionTo(JobStatus.InProgress, manager, null, now);
                break;
            case JobStatus.Completed:
                job.TransitionTo(JobStatus.InProgress, manager, null, now);
                job.TransitionTo(JobStatus.Completed, manager, null, now);
                break;
            case JobStatus.Cancelled:
                job.TransitionTo(JobStatus.Cancelled, manager, CancelReasons[i % CancelReasons.Length], now);
                break;
        }
    }
}
