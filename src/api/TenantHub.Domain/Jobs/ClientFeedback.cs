namespace TenantHub.Domain.Jobs;

/// <summary>The client's sign-off on a completed visit (SPEC-V2 5.6).</summary>
public enum ClientFeedback
{
    Confirmed,
    ProblemReported,
}

public static class ClientFeedbacks
{
    public const int TextMaxLength = 2000;

    public static string Label(ClientFeedback feedback) => feedback switch
    {
        ClientFeedback.Confirmed => "Confirmed",
        _ => "Problem reported",
    };
}
