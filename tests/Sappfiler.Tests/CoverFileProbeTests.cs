using GameTimeTracker.Core.Covers;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 封面文件的内容级探测（Daily Icon 链路的第 1 环）。
/// 硬约束：**不得按扩展名**判 content type —— 本地缓存里确实存在 .jpg 实为 PNG 的文件。
/// </summary>
public class CoverFileProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sapf-probe-" + Guid.NewGuid().ToString("N"));

    public CoverFileProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly byte[] PngBytes =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG magic
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    };

    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    // ① 硬约束：PNG 内容 + .jpg 扩展名 ⇒ 必须判成 image/png
    [Fact]
    public void DetectContentType_PngContentWithJpgExtension_IsPng()
    {
        var path = Write("steam_3265700.jpg", PngBytes);
        Assert.Equal("image/png", CoverFileProbe.DetectContentType(path));
    }

    [Fact]
    public void DetectContentType_JpegContent_IsJpeg()
    {
        var path = Write("a.jpg", JpegBytes);
        Assert.Equal("image/jpeg", CoverFileProbe.DetectContentType(path));
    }

    [Fact]
    public void DetectContentType_UnknownContent_IsOctetStream()
    {
        var path = Write("a.jpg", new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
        Assert.Equal("application/octet-stream", CoverFileProbe.DetectContentType(path));
    }

    [Fact]
    public void IsUsable_FalseForMissingOrEmpty()
    {
        Assert.False(CoverFileProbe.IsUsable(null));
        Assert.False(CoverFileProbe.IsUsable(Path.Combine(_dir, "nope.png")));
        Assert.False(CoverFileProbe.IsUsable(Write("empty.png", Array.Empty<byte>())));
        Assert.True(CoverFileProbe.IsUsable(Write("ok.png", PngBytes)));
    }

    [Fact]
    public void Sha256Hex_IsContentBased_StableAndSensitiveToChange()
    {
        var p1 = Write("c1.png", PngBytes);
        var h1 = CoverFileProbe.Sha256Hex(p1);
        Assert.Equal(h1, CoverFileProbe.Sha256Hex(p1));
        Assert.Equal(64, h1.Length);

        var p2 = Write("c2.png", JpegBytes);
        Assert.NotEqual(h1, CoverFileProbe.Sha256Hex(p2));

        // 同一路径内容变化 ⇒ 哈希必须变（"封面换图"必须触发重新上传）
        File.WriteAllBytes(p1, JpegBytes);
        Assert.NotEqual(h1, CoverFileProbe.Sha256Hex(p1));
    }

    [Fact]
    public void ExtensionFor_MatchesDetectedType()
    {
        Assert.Equal(".png", CoverFileProbe.ExtensionFor("image/png"));
        Assert.Equal(".jpg", CoverFileProbe.ExtensionFor("image/jpeg"));
        Assert.Equal(".bin", CoverFileProbe.ExtensionFor("application/octet-stream"));
    }
}
