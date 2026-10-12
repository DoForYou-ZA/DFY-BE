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
}
