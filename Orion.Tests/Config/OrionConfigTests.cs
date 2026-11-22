using Orion.Config;
using Xunit;

namespace Orion.Tests.Config;

public class OrionConfigTests
{
    [Fact]
    public void ANewConfig_IsCurrent_AndScansEightyYalms()
    {
        var config = new OrionConfig();

        Assert.False(config.Upgrade());
        Assert.Equal(80f, config.Farm.ScanRadiusYalms);
    }

    [Fact]
    public void AVersionOneConfig_StillOnTheOldScanDefault_TakesTheNewOne()
    {
        var config = new OrionConfig { Version = 1 };
        config.Farm.ScanRadiusYalms = 50f;

        Assert.True(config.Upgrade());

        Assert.Equal(80f, config.Farm.ScanRadiusYalms);
        Assert.Equal(OrionConfig.CurrentVersion, config.Version);
        Assert.False(config.Upgrade()); // once
    }

    [Fact]
    public void AVersionOneConfig_WithAScanRadiusThePlayerSet_KeepsIt()
    {
        var config = new OrionConfig { Version = 1 };
        config.Farm.ScanRadiusYalms = 35f;

        Assert.True(config.Upgrade());

        Assert.Equal(35f, config.Farm.ScanRadiusYalms);
    }
}
