using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Infrastructure.Seed;

/// <summary>One step of an organization's invented history, replayed in time order through the domain.</summary>
internal abstract record SeedStep(DateTimeOffset At, string ActorEmail);

internal sealed record CreateClientStep(DateTimeOffset At, string ActorEmail, int Client, ClientDetails Details) : SeedStep(At, ActorEmail);

internal sealed record UpdateClientStep(DateTimeOffset At, string ActorEmail, int Client, ClientDetails Details) : SeedStep(At, ActorEmail);

internal sealed record ArchiveClientStep(DateTimeOffset At, string ActorEmail, int Client) : SeedStep(At, ActorEmail);

internal sealed record CreateJobStep(
    DateTimeOffset At,
    string ActorEmail,
    int Job,
    string Title,
    string? Description,
    int Client,
    string? AssigneeEmail,
    JobPriority Priority,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    decimal? QuotedAmount) : SeedStep(At, ActorEmail);

internal sealed record MoveJobStep(DateTimeOffset At, string ActorEmail, int Job, JobStatus To, string? Reason) : SeedStep(At, ActorEmail);

/// <summary>Who is in a seeded organization, and with which role.</summary>
public sealed record SeedPerson(string Email, string DisplayName, Role Role);

/// <summary>An organization to seed: who is in it, and its history from a fixed random seed.</summary>
internal sealed record SeedOrganization(string Name, string Slug, string TimeZone, Plan Plan, int RandomSeed, IReadOnlyList<SeedPerson> People);

/// <summary>
/// The invented history of a demo organization (SPEC 8.3): clients signed up over the first weeks, jobs planned,
/// scheduled, worked and closed by the people who would do that, from about four months ago to a month ahead.
/// Deterministic for a given seed and "today", and always relative to today, so the demo never looks stale.
/// </summary>
internal static class DemoPlan
{
    private static readonly string[] Tasks =
    [
        "Weekly office clean", "Deep clean of the kitchen area", "Window cleaning, all floors", "Carpet shampoo",
        "Post-renovation clean", "Floor polishing", "Sanitize touchpoints and restrooms", "Restock supplies and paper goods",
        "Move-out clean", "Reception and lobby refresh", "Upholstery steam clean", "Waste room and bins deep clean",
    ];

    private static readonly string[] GardenTasks =
    [
        "Lawn mowing and edging", "Hedge trimming", "Spring planting", "Leaf clearance", "Irrigation check",
        "Tree pruning", "Patio pressure wash", "Mulching the borders", "Pond maintenance", "Weed control",
    ];

    private static readonly string[] CancelReasons =
    [
        "Client postponed the contract", "Duplicate of another booking", "Building closed for repairs",
        "Client chose another date", "Weather made the work unsafe",
    ];

    /// <param name="firstDay">Where the jobs start, in days from today (negative is the past).</param>
    /// <param name="span">How many days the jobs cover from there.</param>
    public static List<SeedStep> For(
        SeedOrganization organization,
        IReadOnlyList<ClientDetails> clients,
        int archivedEvery,
        int jobCount,
        int firstDay,
        int span,
        DateTimeOffset now)
    {
        var random = new Random(organization.RandomSeed);
        var today = Organization.TodayIn(organization.TimeZone, now);
        var start = today.AddDays(-125);
        var steps = new List<SeedStep>();
        DateTimeOffset At(DateOnly day, double hour)
        {
            var at = Organization.StartOfDayIn(organization.TimeZone, day).AddHours(hour);
            return at < now ? at : now.AddMinutes(-random.Next(5, 240));
        }

        var managers = organization.People.Where(p => p.Role is Role.Owner or Role.Admin).ToList();
        var workers = organization.People.Where(p => p.Role is not Role.Viewer).ToList();
        string Manager() => managers[random.Next(managers.Count)].Email;

        // Clients sign up during the first five weeks; a few get a new email later; every n-th is archived.
        for (var c = 0; c < clients.Count; c++)
        {
            var created = start.AddDays(random.Next(0, 35));
            steps.Add(new CreateClientStep(At(created, 9 + random.Next(0, 8)), Manager(), c, clients[c]));
            if (c % 5 == 2 && clients[c].Email is { } email)
            {
                var updated = clients[c] with { Email = "office." + email };
                steps.Add(new UpdateClientStep(At(created.AddDays(random.Next(10, 60)), 10), Manager(), c, updated));
            }

            if (archivedEvery > 0 && c % archivedEvery == archivedEvery - 1)
            {
                steps.Add(new ArchiveClientStep(At(created.AddDays(random.Next(40, 80)), 15), Manager(), c));
            }
        }

        var active = Enumerable.Range(0, clients.Count).Where(c => archivedEvery == 0 || c % archivedEvery != archivedEvery - 1).ToList();
        var tasks = organization.Plan == Plan.Free ? GardenTasks : Tasks;
        var priorities = new[] { JobPriority.Normal, JobPriority.Low, JobPriority.Normal, JobPriority.High, JobPriority.Urgent, JobPriority.Normal };

        for (var j = 0; j < jobCount; j++)
        {
            // Spread from four months back to a month ahead, a little jitter so days are uneven.
            var offset = firstDay + (int)Math.Round(j * (double)span / jobCount) + random.Next(-1, 2);
            var scheduled = today.AddDays(offset);
            var target = TargetStatus(random, offset);
            var client = active[random.Next(active.Count)];
            var worker = workers[random.Next(workers.Count)];
            var assignee = target == JobStatus.New && random.Next(3) == 0 ? null : worker.Email;
            var created = Max(start.AddDays(36), scheduled.AddDays(-random.Next(3, 15)));
            if (created > today)
            {
                created = today;
            }

            var createdAt = At(created, 8 + random.Next(0, 9) + (random.Next(0, 60) / 60.0));
            var task = tasks[random.Next(tasks.Length)];
            steps.Add(new CreateJobStep(
                createdAt,
                Manager(),
                j,
                $"{task} – {clients[client].Name}",
                random.Next(3) == 0 ? $"Access through the main entrance. Contact: {clients[client].ContactName ?? "front desk"}." : null,
                client,
                assignee,
                priorities[random.Next(priorities.Length)],
                target == JobStatus.New ? null : scheduled,
                random.Next(9) == 0 ? null : scheduled.AddDays(random.Next(0, 4)),
                random.Next(6) == 0 ? null : Math.Round(60m + random.Next(0, 180) * 7.5m, 2)));

            // The job's moves, each a while after the one before; then kept strictly in order and in the past.
            var moves = new List<(double Hours, string Actor, JobStatus To, string? Reason)>();
            switch (target)
            {
                case JobStatus.Scheduled:
                    moves.Add((1 + random.NextDouble() * 29, Manager(), JobStatus.Scheduled, null));
                    break;
                case JobStatus.InProgress:
                case JobStatus.Completed:
                    moves.Add((1 + random.NextDouble() * 29, Manager(), JobStatus.Scheduled, null));
                    // Started on the scheduled morning (or soon after it was scheduled, if that was later).
                    var startedAt = At(scheduled, 8 + random.Next(0, 3));
                    moves.Add((Math.Max(0.5, (startedAt - createdAt).TotalHours - moves[0].Hours), assignee!, JobStatus.InProgress, null));
                    if (target == JobStatus.Completed)
                    {
                        moves.Add((2 + random.NextDouble() * 5, assignee!, JobStatus.Completed, null));
                    }

                    break;
                case JobStatus.Cancelled:
                    moves.Add((24 + random.NextDouble() * 24 * 5, Manager(), JobStatus.Cancelled, CancelReasons[random.Next(CancelReasons.Length)]));
                    break;
            }

            var times = new List<DateTimeOffset>();
            var at = createdAt;
            foreach (var move in moves)
            {
                at = at.AddHours(move.Hours);
                times.Add(at);
            }

            // Anything that would fall in the future happens in the last minutes instead, in the same order.
            for (var m = times.Count - 1; m >= 0; m--)
            {
                var latest = m == times.Count - 1 ? now.AddMinutes(-1) : times[m + 1].AddMinutes(-1);
                if (times[m] > latest)
                {
                    times[m] = latest;
                }
            }

            for (var m = 0; m < moves.Count; m++)
            {
                var when = times[m] > createdAt ? times[m] : createdAt.AddSeconds(m + 1);
                steps.Add(new MoveJobStep(when, moves[m].Actor, j, moves[m].To, moves[m].Reason));
            }
        }

        // Stable: time first, then creation before changes, then plan order.
        return [.. steps.Select((step, index) => (step, index)).OrderBy(s => s.step.At).ThenBy(s => s.index).Select(s => s.step)];
    }

    private static JobStatus TargetStatus(Random random, int offset)
    {
        var roll = random.Next(100);
        if (offset > 0)
        {
            return roll < 45 ? JobStatus.New : JobStatus.Scheduled;
        }

        if (offset > -21)
        {
            // The last three weeks: work in flight, a few things slipping (overdue), most of it done.
            return roll switch
            {
                < 40 => JobStatus.Completed,
                < 68 => JobStatus.InProgress,
                < 78 => JobStatus.Scheduled,
                < 90 => JobStatus.New,
                _ => JobStatus.Cancelled,
            };
        }

        // Older work is done, with some cancellations and a few jobs nobody closed.
        return roll switch
        {
            < 82 => JobStatus.Completed,
            < 92 => JobStatus.Cancelled,
            < 96 => JobStatus.InProgress,
            _ => JobStatus.Scheduled,
        };
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

}
