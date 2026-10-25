using System.Globalization;
using TenantHub.Domain.Clients;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// Fictional clients (SPEC 8.3): offices, clinics, schools and homes in invented places, with reserved
/// <c>.example</c> emails and obviously fake phone numbers. Deterministic, so every seed looks the same.
/// </summary>
internal static class DemoClients
{
    private static readonly string[] Places =
    [
        "Northgate", "Riverside", "Oakfield", "Maple Court", "Lakeview", "Harborline", "Westmere", "Cedar Point",
        "Elmstead", "Kingsbridge",
    ];

    private static readonly string[] Contacts =
    [
        "Dana Reyes", "Tomas Varga", "Aisha Bello", "Mikkel Strand", "Priya Natarajan", "Lena Hoffmann", "Jonah Pike",
        "Rosa Delgado", "Kofi Mensah", "Elin Berg", "Marco Conti", "Hana Sato", "Ines Duarte",
    ];

    /// <summary>Forty clients of a cleaning company: every place has offices, a clinic, a school and a studio.</summary>
    public static IReadOnlyList<ClientDetails> CleaningCompany() =>
        Build(Places, [("Offices", "offices"), ("Dental Clinic", "dental"), ("Primary School", "school"), ("Fitness Studio", "fitness")], 100);

    /// <summary>Twenty-four clients of a landscaper: homes, parks and a few businesses.</summary>
    public static IReadOnlyList<ClientDetails> Landscaper() =>
        Build(Places[..8], [("Gardens", "gardens"), ("Community Park", "park"), ("Business Centre", "centre")], 300);

    private static List<ClientDetails> Build(string[] places, (string Kind, string Domain)[] kinds, int phoneBase)
    {
        var clients = new List<ClientDetails>();
        var n = 0;
        foreach (var place in places)
        {
            foreach (var (kind, domain) in kinds)
            {
                var slug = $"{place.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()}-{domain}";
                var contact = Contacts[n % Contacts.Length];
                clients.Add(ClientDetails.Validate(
                    $"{place} {kind}",
                    contact,
                    $"{contact.Split(' ')[0].ToLowerInvariant()}@{slug}.example",
                    $"+00 {(phoneBase + n).ToString(CultureInfo.InvariantCulture)} 000 000",
                    $"{(n % 90) + 1} {place} Road",
                    n % 5 == 0 ? "Key safe by the side entrance; alarm code from the office manager." : null));
                n++;
            }
        }

        return clients;
    }
}
