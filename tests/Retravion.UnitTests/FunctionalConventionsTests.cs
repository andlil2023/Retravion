using CSharpFunctionalExtensions;
using Xunit;

namespace Retravion.UnitTests;

public sealed class FunctionalConventionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FluentCompositionRunsSuccessStepsOnlyWhenValidationSucceeds(bool valid)
    {
        var bound = false;
        var tapped = false;

        var result = Result.Success(2)
            .Ensure(_ => valid, "Invalid input")
            .Bind(value =>
            {
                bound = true;
                return Result.Success(value + 1);
            })
            .Map(value => value * 2)
            .Tap(_ => { tapped = true; });

        Assert.Equal(valid, result.IsSuccess);
        Assert.Equal(valid, bound);
        Assert.Equal(valid, tapped);
        if (valid)
        {
            Assert.Equal(6, result.Value);
        }
        else
        {
            Assert.Equal("Invalid input", result.Error);
        }
    }

    [Fact]
    public void SuccessfulLookupWithNoMatchIsDistinctFromFailedLookup()
    {
        var absent = Result.Success(Maybe<string>.None);
        var failed = Result.Failure<Maybe<string>>("Lookup unavailable");

        Assert.True(absent.IsSuccess);
        Assert.True(absent.Value.HasNoValue);
        Assert.True(failed.IsFailure);
        Assert.Equal("Lookup unavailable", failed.Error);

        var required = absent.Value.ToResult("Value is required");
        Assert.True(required.IsFailure);
        Assert.Equal("Value is required", required.Error);
    }

    [Fact]
    public void OperationWithoutReturnValueCanReportExpectedFailure()
    {
        Result success = Result.Success();
        Result failure = Result.Failure("Operation rejected");

        Assert.True(success.IsSuccess);
        Assert.True(failure.IsFailure);
        Assert.Equal("Operation rejected", failure.Error);
    }
}
