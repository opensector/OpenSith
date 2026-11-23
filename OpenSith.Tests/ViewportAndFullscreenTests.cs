using System.Numerics;
using OpenSith.Client;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Xunit;

namespace OpenSith.Tests;

public class ViewportAndFullscreenTests
{
    [Fact]
    public void ResolveViewportSize_WindowedMaximizedAndFullscreen_ReturnsDrawablePixels()
    {
        Assert.Equal(
            new Vector2D<int>(1280, 720),
            Program.ResolveViewportSize(new Vector2D<int>(1280, 720), new Vector2D<int>(1280, 720), WindowState.Normal));

        Assert.Equal(
            new Vector2D<int>(1920, 1009),
            Program.ResolveViewportSize(new Vector2D<int>(1920, 1009), new Vector2D<int>(1920, 1009), WindowState.Maximized));

        Assert.Equal(
            new Vector2D<int>(2560, 1440),
            Program.ResolveViewportSize(new Vector2D<int>(2560, 1440), new Vector2D<int>(2560, 1440), WindowState.Fullscreen));
    }

    [Fact]
    public void ResolveViewportSize_HighDpi_PrefersFramebufferSizeOverLogicalWindowSize()
    {
        Vector2D<int> resolved = Program.ResolveViewportSize(
            framebufferSize: new Vector2D<int>(2560, 1440),
            windowSize: new Vector2D<int>(1280, 720),
            WindowState.Normal);

        Assert.Equal(new Vector2D<int>(2560, 1440), resolved);
    }

    [Fact]
    public void ResolveViewportSize_MinimizedOrZeroDimensions_ReturnsZero()
    {
        Assert.Equal(
            Vector2D<int>.Zero,
            Program.ResolveViewportSize(new Vector2D<int>(1280, 720), new Vector2D<int>(1280, 720), WindowState.Minimized));

        Assert.Equal(
            new Vector2D<int>(1920, 1080),
            Program.ResolveViewportSize(Vector2D<int>.Zero, new Vector2D<int>(1920, 1080), WindowState.Normal));

        Assert.Equal(
            Vector2D<int>.Zero,
            Program.ResolveViewportSize(Vector2D<int>.Zero, Vector2D<int>.Zero, WindowState.Normal));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    [InlineData(1024, 768)]
    public void TryWindowToVirtual_FullscreenAndWidescreen_MapsCenterAndRejectsPillarboxMargins(int width, int height)
    {
        bool insideCenter = MenuRenderer.TryWindowToVirtual(width * 0.5f, height * 0.5f, width, height, out Vector2 center);
        Assert.True(insideCenter);
        Assert.InRange(center.X, 319.5f, 320.5f);
        Assert.InRange(center.Y, 239.5f, 240.5f);

        float scale = MathF.Min(width / MenuRenderer.VirtualWidth, height / MenuRenderer.VirtualHeight);
        float offsetX = (width - MenuRenderer.VirtualWidth * scale) * 0.5f;
        if (offsetX > 2.0f)
        {
            Assert.False(MenuRenderer.TryWindowToVirtual(1.0f, height * 0.5f, width, height, out _));
            Assert.False(MenuRenderer.TryWindowToVirtual(width - 1.0f, height * 0.5f, width, height, out _));
        }
    }
}
