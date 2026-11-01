using OpenPartition.Operations;

namespace OpenPartition.Tests;

public class ShadowCopiesTests
{
    private const string Volume = @"\\?\Volume{12345678-1234-1234-1234-123456789abc}\";

    [Fact]
    public void ConfirmationProducesOnlyVolumeScopedArguments()
    {
        Assert.Equal(new[] { "delete", "shadows", "/for=" + Volume, "/all", "/quiet" }, ShadowCopies.DeleteArguments(Volume, Volume));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("C:")]
    [InlineData(@"\\?\Volume{aaaaaaaa-1234-1234-1234-123456789abc}\")]
    public void ConfirmationMustNameExactVolume(string confirmation) =>
        Assert.Throws<PlanException>(() => ShadowCopies.DeleteArguments(Volume, confirmation));

    [Theory]
    [InlineData("C: /all")]
    [InlineData("/all")]
    [InlineData(@"\\?\Volume{zzzzzzzz-1234-1234-1234-123456789abc}\")]
    [InlineData("C:")]
    public void InvalidTargetsNeverBuildCommands(string target) =>
        Assert.Throws<PlanException>(() => ShadowCopies.DeleteArguments(target, target));
}
