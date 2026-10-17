using DoForYou.API.Models;
using DoForYou.API.Services;

namespace DoForYou.Tests;

public class UserPolicyServiceTests
{
    private readonly UserPolicyService _policy = new();

    private static User CompleteUser(string userType = "both") => new()
    {
        FirstName = "Creator",
        LastName = "Demo",
        Email = "creator@example.com",
        EmailVerified = true,
        PhoneNumber = "0821234567",
        UserType = userType,
        IdentityVerificationStatus = "passed",
        AuthenticatorTwoFactorEnabled = true
        // Address, ID and DOB are intentionally absent: none is a basic signup field.
    };

    [Theory]
    [InlineData("creator", true, false)]
    [InlineData("Creator", true, false)]
    [InlineData("runner", false, true)]
    [InlineData("Runner", false, true)]
    [InlineData("both", true, true)]
    [InlineData("Both", true, true)]
    public void Capabilities_follow_preference_and_independent_verification(
        string userType,
        bool canCreate,
        bool canAccept)
    {
        var user = CompleteUser(userType);

        Assert.True(_policy.IsProfileComplete(user));
        Assert.Equal(100, _policy.GetProfileCompletion(user));
        Assert.Empty(_policy.GetMissingProfileFields(user));
        Assert.Equal(canCreate, _policy.CanCreateTasks(user));
        Assert.Equal(canAccept, _policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Creator_capability_does_not_require_address_ID_or_DOB()
    {
        var user = CompleteUser("both");
        user.Address = null;
        user.IdNumber = null;
        user.DateOfBirth = null;

        Assert.True(_policy.CanCreateTasks(user));
        Assert.True(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Profile_completion_is_informational_and_does_not_gate_creator_capability()
    {
        var user = CompleteUser("creator");
        user.FirstName = "";

        Assert.False(_policy.IsProfileComplete(user));
        Assert.Contains("firstName", _policy.GetMissingProfileFields(user));
        Assert.True(_policy.CanCreateTasks(user));
    }

    [Fact]
    public void Runner_cannot_accept_without_passed_identity_and_authenticator_2FA()
    {
        var user = CompleteUser("runner");
        user.IdentityVerificationStatus = "not_started";
        user.AuthenticatorTwoFactorEnabled = false;

        Assert.False(_policy.CanAcceptTasks(user));

        user.IdentityVerificationStatus = "passed";
        Assert.False(_policy.CanAcceptTasks(user));

        user.IdentityVerificationStatus = "pending";
        user.AuthenticatorTwoFactorEnabled = true;
        Assert.False(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Passed_runner_identity_and_2FA_are_independent_of_profile_address_ID_and_payout_setup()
    {
        var user = CompleteUser("runner");
        user.Address = null;
        user.IdNumber = null;
        user.DateOfBirth = null;

        Assert.True(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Selecting_both_does_not_grant_runner_eligibility()
    {
        var user = CompleteUser("creator");
        user.IdentityVerificationStatus = "not_started";
        user.AuthenticatorTwoFactorEnabled = false;
        user.UserType = "both";

        Assert.True(_policy.CanCreateTasks(user));
        Assert.False(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Unverified_email_cannot_enable_task_capabilities()
    {
        var user = CompleteUser("both");
        user.EmailVerified = false;

        Assert.False(_policy.CanCreateTasks(user));
        Assert.False(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Archived_account_cannot_use_task_capabilities()
    {
        var user = CompleteUser("both");
        user.IsArchived = true;

        Assert.False(_policy.CanCreateTasks(user));
        Assert.False(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Payout_eligibility_is_checked_separately_and_does_not_depend_on_role_preference()
    {
        var user = CompleteUser("creator");

        Assert.True(_policy.CanReceivePayout(user));

        // Preference changes never erase a financial obligation already earned.
        user.UserType = "runner";
        Assert.True(_policy.CanReceivePayout(user));

        user.UserType = "both";
        Assert.True(_policy.CanReceivePayout(user));
    }

    [Fact]
    public void Payout_is_held_when_email_identity_or_authenticator_gate_is_missing()
    {
        var user = CompleteUser("runner");
        Assert.True(_policy.CanReceivePayout(user));

        user.EmailVerified = false;
        Assert.False(_policy.CanReceivePayout(user));

        user.EmailVerified = true;
        user.IdentityVerificationStatus = "pending";
        Assert.False(_policy.CanReceivePayout(user));

        user.IdentityVerificationStatus = "passed";
        user.AuthenticatorTwoFactorEnabled = false;
        Assert.False(_policy.CanReceivePayout(user));

        user.AuthenticatorTwoFactorEnabled = true;
        user.IsArchived = true;
        Assert.False(_policy.CanReceivePayout(user));
    }

    [Fact]
    public void Valid_adult_id_derives_the_encoded_date_of_birth()
    {
        const string validId = "8001015009087";

        Assert.True(_policy.TryGetAdultDateOfBirthFromSouthAfricanId(validId, out var dateOfBirth));
        Assert.Equal(new DateTime(1980, 1, 1), dateOfBirth.Date);
    }

    [Fact]
    public void Underage_id_is_rejected_even_when_checksum_is_valid()
    {
        var underageId = WithValidChecksum("100101500908");

        Assert.False(_policy.TryGetAdultDateOfBirthFromSouthAfricanId(underageId, out _));
    }

    [Fact]
    public void Invalid_calendar_date_is_rejected_even_when_checksum_is_valid()
    {
        var invalidDateId = WithValidChecksum("801332500908");

        Assert.False(_policy.TryGetAdultDateOfBirthFromSouthAfricanId(invalidDateId, out _));
    }

    private string WithValidChecksum(string firstTwelveDigits)
    {
        Assert.Equal(12, firstTwelveDigits.Length);
        for (var checkDigit = 0; checkDigit <= 9; checkDigit++)
        {
            var candidate = firstTwelveDigits + checkDigit;
            if (_policy.IsValidSouthAfricanId(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Unable to construct a valid checksum test ID.");
    }
}
