using System.Globalization;
using TenantHub.Domain.Clients;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// Forty fictional clients of a cleaning company (SPEC 8.3): offices, clinics, schools and homes in invented
/// places, with reserved <c>.example</c> emails and obviously fake phone numbers. Deterministic, so every seed
/// looks the same; every eighth one is archived.
/// </summary>
internal static class DemoClients
{
    private static readonly string[] Places =
    [
        "Northgate", "Riverside", "Oakfield", "Maple Court", "Lakeview", "Harborline", "Westmere", "Cedar Point",
        "Elmstead", "Kingsbridge",
    ];

    private static readonly (string Kind, string Domain)[] Kinds =
    [
        ("Offices", "offices"), ("Dental Clinic", "dental"), ("Primary School", "school"), ("Fitness Studio", "fitness"),
    ];

    private static readonly string[] Contacts =
    [
        "Dana Reyes", "Tomas Varga", "Aisha Bello", "Mikkel Strand", "Priya Natarajan", "Lena Hoffmann", "Jonah Pike",
        "Rosa Delgado", "Kofi Mensah", "Elin Berg", "Marco Conti", "Hana Sato", "Ines Duarte",
    ];

    public static IEnumerable<Client> Create(Guid organizationId, DateTimeOffset now)
    {
        var n = 0;
        foreach (var place in Places)
        {
            foreach (var (kind, domain) in Kinds)
            {
                var slug = $"{place.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()}-{domain}";
                var contact = Contacts[n % Contacts.Length];
                var details = ClientDetails.Validate(
                    $"{place} {kind}",
                    contact,
                    $"{contact.Split(' ')[0].ToLowerInvariant()}@{slug}.example",
                    $"+00 {(100 + n).ToString(CultureInfo.InvariantCulture)} 000 000",
                    $"{(n % 90) + 1} {place} Road",
                    n % 5 == 0 ? "Key safe by the side entrance; alarm code from the office manager." : null);

                var client = Client.Create(organizationId, details, now);
                if (n % 8 == 7)
                {
                    client.Archive(now);
                }

                n++;
                yield return client;
            }
        }
    }
}
