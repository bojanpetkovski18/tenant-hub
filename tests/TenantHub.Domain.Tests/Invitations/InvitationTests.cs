using TenantHub.Domain.Invitations;
using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Tests.Invitations;

public sealed class InvitationTests
{
    private static readonly Guid Org = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly Actor Owner = new(Guid.Parse("0199f0a0-0000-7000-8000-000000000001"), Role.Owner);
    private static readonly Guid Joiner = Guid.Parse("0199f0a0-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Hash1 = [1, 2, 3];
    private static readonly byte[] Hash2 = [4, 5, 6];

    private static Invitation New(string email = "Maya.Torres@Brightline.example", Role role = Role.Member) =>
        Invitation.Create(Org, email, role, Hash1, Owner, Now);

    private static string? Outcome(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (DomainException error)
        {
            return error.Code;
        }
    }

    [Fact]
    public void A_new_invitation_is_pending_for_seven_days()
    {
        var invitation = New();

        Assert.Equal(("Maya.Torres@Brightline.example", "maya.torres@brightline.example"), (invitation.Email, invitation.NormalizedEmail));
        Assert.Equal(Now.AddDays(7), invitation.ExpiresAt);
        Assert.Equal(InvitationState.Pending, invitation.StateAt(Now.AddDays(7).AddTicks(-1)));
        Assert.Equal(InvitationState.Expired, invitation.StateAt(Now.AddDays(7)));
        Assert.Equal("Invited Maya.Torres@Brightline.example as Member", ((InvitationEvent)invitation.DomainEvents.Single()).Summary);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public void The_email_must_be_valid(string email)
    {
        var error = Assert.Throws<DomainValidationException>(() => New(email));

        Assert.Contains("email", error.Errors.Keys);
    }

    [Fact]
    public void Nobody_is_invited_as_owner() =>
        Assert.Equal("owner-not-invitable", Outcome(() => New(role: Role.Owner)));

    [Fact]
    public void Accepting_needs_the_invited_email_ignoring_case_and_spaces()
    {
        Assert.Equal("invitation-email-mismatch", Outcome(() => New().Accept(Joiner, "someone@else.example", Now)));

        var invitation = New();
        invitation.Accept(Joiner, "  MAYA.torres@brightline.EXAMPLE ", Now.AddDays(1));

        Assert.Equal(InvitationState.Accepted, invitation.StateAt(Now.AddDays(1)));
        Assert.Equal((Now.AddDays(1), Joiner), (invitation.AcceptedAt, invitation.AcceptedByUserId));
    }

    public static TheoryData<string, string> ClosedStates => new()
    {
        { "expired", "invitation-expired" },
        { "revoked", "invitation-revoked" },
        { "accepted", "invitation-used" },
    };

    [Theory]
    [MemberData(nameof(ClosedStates))]
    public void A_closed_invitation_cannot_be_accepted(string state, string refusal)
    {
        var invitation = New();
        var at = Now.AddDays(1);
        switch (state)
        {
            case "expired":
                at = Now.AddDays(8);
                break;
            case "revoked":
                invitation.Revoke(Now);
                break;
            case "accepted":
                invitation.Accept(Joiner, invitation.Email, Now);
                break;
        }

        Assert.Equal(refusal, Outcome(() => invitation.Accept(Guid.NewGuid(), invitation.Email, at)));
    }

    [Fact]
    public void Resending_rotates_the_token_and_restarts_the_week_even_after_expiry()
    {
        var invitation = New();

        invitation.Resend(Hash2, Role.Viewer, Owner, Now.AddDays(10));

        Assert.Equal(Hash2, invitation.TokenHash);
        Assert.Equal(Now.AddDays(17), invitation.ExpiresAt);
        Assert.Equal(Role.Viewer, invitation.Role);
        Assert.Equal(InvitationState.Pending, invitation.StateAt(Now.AddDays(10)));
        Assert.Equal(new FieldChange("role", "Member", "Viewer"), ((InvitationEvent)invitation.DomainEvents[^1]).Changes.Single());
    }

    [Fact]
    public void Used_or_withdrawn_invitations_cannot_be_resent_or_revoked()
    {
        var accepted = New();
        accepted.Accept(Joiner, accepted.Email, Now);
        var revoked = New();
        revoked.Revoke(Now);

        Assert.Equal("invitation-closed", Outcome(() => accepted.Resend(Hash2, Role.Member, Owner, Now)));
        Assert.Equal("invitation-closed", Outcome(() => accepted.Revoke(Now)));
        Assert.Equal("invitation-closed", Outcome(() => revoked.Resend(Hash2, Role.Member, Owner, Now)));
    }

    [Fact]
    public void Audit_summaries_never_contain_the_token()
    {
        var invitation = New();
        invitation.Resend(Hash2, Role.Member, Owner, Now);
        invitation.Revoke(Now);

        Assert.All(invitation.DomainEvents.Cast<InvitationEvent>(), e =>
            Assert.DoesNotContain(Convert.ToHexString(Hash2), e.Summary + string.Join(",", e.Changes), StringComparison.OrdinalIgnoreCase));
    }
}
