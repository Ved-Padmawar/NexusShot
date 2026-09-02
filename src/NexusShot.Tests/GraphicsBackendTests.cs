using NexusShot.Render;

namespace NexusShot.Tests;

public class GraphicsBackendTests
{
    [Theory]
    [InlineData(0x8086u, 0x7D51u, 0x00200000006517D8L, true)]
    [InlineData(0x8086u, 0x7D51u, 0x00200000006517D9L, false)]
    [InlineData(0x8086u, 0x7D51u, 0x00200000006517D7L, false)]
    [InlineData(0x8086u, 0x7D55u, 0x00200000006517D8L, false)]
    [InlineData(0x10DEu, 0x7D51u, 0x00200000006517D8L, false)]
    [InlineData(0x8086u, 0x7D51u, 0L, false)]
    public void WorkaroundMatchesOnlyTheReproducedConfiguration(uint vendor, uint device, long driver, bool expected)
    {
        Assert.Equal(expected, GraphicsBackend.RequiresSoftwareRendering(vendor, device, driver));
    }
}
