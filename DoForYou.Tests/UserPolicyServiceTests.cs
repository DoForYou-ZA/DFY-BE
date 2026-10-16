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
        Address = "Sandton",
        IdNumber = "8001015009087",
        DateOfBirth = new DateTime(1980, 1, 1),
        UserType = userType
    };

    [Theory]
    [InlineData("creator", true, false)]
    [InlineData("Creator", true, false)]
    [InlineData("runner", false, true)]
    [InlineData("Runner", false, true)]
    [InlineData("both", true, true)]
    [InlineData("Both", true, true)]
    public void Complete_user_gets_only_backend_allowed_capabilities(
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
    public void Incomplete_both_user_cannot_create_or_accept()
    {
        var user = CompleteUser("both");
        user.Address = "just around";

        Assert.False(_policy.IsProfileComplete(user));
        Assert.Equal(89, _policy.GetProfileCompletion(user));
        Assert.Contains("address", _policy.GetMissingProfileFields(user));
        Assert.False(_policy.CanCreateTasks(user));
        Assert.False(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Invalid_id_is_reported_as_missing()
    {
        var user = CompleteUser("runner");
        user.IdNumber = "1234567890123";

        Assert.False(_policy.IsProfileComplete(user));
        Assert.Contains("idNumber", _policy.GetMissingProfileFields(user));
        Assert.False(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void Placeholder_address_is_not_accepted()
    {
        var user = CompleteUser("creator");
        user.Address = "near me";

        Assert.False(_policy.IsProfileComplete(user));
        Assert.Contains("address", _policy.GetMissingProfileFields(user));
        Assert.False(_policy.CanCreateTasks(user));
    }
    [Fact]
    public void Valid_adult_id_derives_the_encoded_date_of_birth()
    {
        var validId = "8001015009087";

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

    [Fact]
    public void Unverified_email_cannot_enable_task_capabilities()
    {
        var user = CompleteUser("both");
        user.EmailVerified = false;

        Assert.False(_policy.CanCreateTasks(user));
        Assert.False(_policy.CanAcceptTasks(user));
    }

    [Fact]
    public void DOB_must_match_the_valid_adult_id()
    {
        var user = CompleteUser("both");
        user.DateOfBirth = new DateTime(1981, 1, 1);

        Assert.False(_policy.IsProfileComplete(user));
        Assert.Contains("dateOfBirth", _policy.GetMissingProfileFields(user));
        Assert.False(_policy.CanCreateTasks(user));
        Assert.False(_policy.CanAcceptTasks(user));
    }

}
