using OpenPartition.Model;

namespace OpenPartition.Tests;

public class ProductInfoTests
{
    [Fact]
    public void DisclaimerMentionsBackup() =>
        Assert.Contains("backup", ProductInfo.Disclaimer, StringComparison.OrdinalIgnoreCase);
}
