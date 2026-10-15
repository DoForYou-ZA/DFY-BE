using System.ComponentModel.DataAnnotations;
using DoForYou.API.DTOs;

namespace DoForYou.Tests;

public class UpdateTaskRequestTests
{
    [Fact]
    public void TaskNameIsOptionalWhenOnlyOtherFieldsAreUpdated()
    {
        var request = new UpdateTaskRequest { TaskName = null };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.True(isValid);
    }

    [Fact]
    public void TaskNameRejectsValuesLongerThanSixtyCharacters()
    {
        var request = new UpdateTaskRequest { TaskName = new string('x', 61) };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(UpdateTaskRequest.TaskName)));
    }

    [Fact]
    public void TaskNameAcceptsValidValues()
    {
        var request = new UpdateTaskRequest { TaskName = "Assemble a desk" };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.True(isValid);
    }
}
