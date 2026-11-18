using System;
using System.Collections.Generic;
using System.IO;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace Dignite.Vault.Parse.Ocr.VisionLlm;

// These call SkiaSharp natively, unlike the rest of this project's tests (see VisionLlmOcrProviderTests, which
// shrinks only through the provider hook and so loads the same native library).
public class VisionLlmImageDownscalerTests
{
    private const int Budget = 1_500_000;

    [Fact]
    public void An_image_within_the_budget_is_left_alone()
    {
        var jpeg = Encode(Solid(1200, 1000, SKColors.Gray), SKEncodedImageFormat.Jpeg);

        var result = VisionLlmImageDownscaler.Downscale(jpeg, Budget);

        result.Status.ShouldBe(ImageDownscaleStatus.WithinBudget);
        result.Data.ShouldBeNull();
        result.OriginalWidth.ShouldBe(1200);
        result.OriginalHeight.ShouldBe(1000);
    }

    [Fact]
    public void A_large_photo_is_scaled_down_to_the_budget_keeping_its_proportions()
    {
        var jpeg = Encode(Solid(3000, 2000, SKColors.Gray), SKEncodedImageFormat.Jpeg);

        var result = VisionLlmImageDownscaler.Downscale(jpeg, Budget);

        result.Status.ShouldBe(ImageDownscaleStatus.Downscaled);
        result.MediaType.ShouldBe("image/jpeg");
        ((long)result.Width * result.Height).ShouldBeLessThanOrEqualTo(Budget);
        ((double)result.Width * result.Height).ShouldBeGreaterThan(Budget * 0.95);
        ((double)result.Width / result.Height).ShouldBe(1.5, tolerance: 0.01);
        (result.OriginalWidth, result.OriginalHeight).ShouldBe((3000, 2000));

        var (width, height) = ReadSize(result.Data!);
        (width, height).ShouldBe((result.Width, result.Height));
    }

    [Fact]
    public void A_large_png_stays_a_png()
    {
        var png = Encode(Solid(2560, 1600, SKColors.White), SKEncodedImageFormat.Png);

        var result = VisionLlmImageDownscaler.Downscale(png, Budget);

        result.Status.ShouldBe(ImageDownscaleStatus.Downscaled);
        result.MediaType.ShouldBe("image/png");
        ((long)result.Width * result.Height).ShouldBeLessThanOrEqualTo(Budget);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void An_exif_rotation_is_applied_so_the_copy_is_upright(int orientation, bool turnsSideways)
    {
        // 2400 x 1600 as stored: the left half red, the right half blue.
        var jpeg = WithExifOrientation(Encode(LeftRedRightBlue(2400, 1600), SKEncodedImageFormat.Jpeg), orientation);

        var result = VisionLlmImageDownscaler.Downscale(jpeg, Budget);

        result.Status.ShouldBe(ImageDownscaleStatus.Downscaled);
        (result.Height > result.Width).ShouldBe(turnsSideways);

        using var bitmap = SKBitmap.Decode(result.Data!);
        var left = bitmap.GetPixel(bitmap.Width / 10, bitmap.Height / 2);
        var right = bitmap.GetPixel(bitmap.Width * 9 / 10, bitmap.Height / 2);
        var top = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 10);
        var bottom = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height * 9 / 10);

        // Where the red half (stored on the left) must end up for each orientation.
        switch (orientation)
        {
            case 1: // as stored
            case 4: // flipped top to bottom: left stays left
                IsRed(left).ShouldBeTrue();
                IsBlue(right).ShouldBeTrue();
                break;
            case 2: // mirrored left to right
            case 3: // upside down
                IsBlue(left).ShouldBeTrue();
                IsRed(right).ShouldBeTrue();
                break;
            case 5: // transposed: stored left becomes the top
            case 6: // a quarter turn clockwise: stored left becomes the top
                IsRed(top).ShouldBeTrue();
                IsBlue(bottom).ShouldBeTrue();
                break;
            case 7: // transverse: stored left becomes the bottom
            case 8: // a quarter turn counterclockwise: stored left becomes the bottom
                IsBlue(top).ShouldBeTrue();
                IsRed(bottom).ShouldBeTrue();
                break;
        }
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_reported_unreadable()
    {
        var result = VisionLlmImageDownscaler.Downscale(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Budget);

        result.Status.ShouldBe(ImageDownscaleStatus.Unreadable);
        result.Data.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_budget_of_zero_or_less_switches_shrinking_off(int budget)
    {
        var jpeg = Encode(Solid(3000, 2000, SKColors.Gray), SKEncodedImageFormat.Jpeg);

        VisionLlmImageDownscaler.Downscale(jpeg, budget).Status.ShouldBe(ImageDownscaleStatus.WithinBudget);
    }

    [Fact]
    public void An_image_declaring_more_pixels_than_the_input_cap_is_refused_from_its_header()
    {
        // Ten billion pixels, 40 GB decoded, and PNG has no reduced-size decode: had the size been checked after
        // decoding, the allocation would fail and this would throw instead of answering TooLarge.
        var bomb = DecompressionBomb.HeaderOnlyPng(100_000, 100_000);

        var result = VisionLlmImageDownscaler.Downscale(bomb, Budget);

        result.Status.ShouldBe(ImageDownscaleStatus.TooLarge);
        (result.OriginalWidth, result.OriginalHeight).ShouldBe((100_000, 100_000));
        result.Data.ShouldBeNull();
    }

    [Theory]
    [InlineData(5_999_999, ImageDownscaleStatus.TooLarge)]
    [InlineData(6_000_000, ImageDownscaleStatus.Downscaled)]
    [InlineData(0, ImageDownscaleStatus.Downscaled)]
    [InlineData(-1, ImageDownscaleStatus.Downscaled)]
    public void The_input_cap_counts_declared_pixels_and_zero_or_less_switches_it_off(long cap, ImageDownscaleStatus expected)
    {
        var jpeg = Encode(Solid(3000, 2000, SKColors.Gray), SKEncodedImageFormat.Jpeg);

        VisionLlmImageDownscaler.Downscale(jpeg, Budget, cap).Status.ShouldBe(expected);
    }

    [Fact]
    public void An_image_within_the_budget_is_sent_whatever_the_input_cap()
    {
        var jpeg = Encode(Solid(1200, 1000, SKColors.Gray), SKEncodedImageFormat.Jpeg);

        VisionLlmImageDownscaler.Downscale(jpeg, Budget, maxInputPixels: 1).Status.ShouldBe(ImageDownscaleStatus.WithinBudget);
    }

    [Theory]
    [InlineData(0.25, 1000, 750)]   // exactly an eighth: decoded at that eighth
    [InlineData(0.30, 1500, 1125)]  // between 2/8 and 3/8: the eighth above, never below the target
    [InlineData(0.05, 500, 375)]    // under 1/8: the smallest libjpeg-turbo produces
    public void A_jpeg_is_decoded_at_the_eighth_at_or_above_the_scale(double scale, int width, int height)
    {
        using var codec = Codec(Encode(Solid(4000, 3000, SKColors.Gray), SKEncodedImageFormat.Jpeg));
        var target = ((int)Math.Floor(4000 * scale), (int)Math.Floor(3000 * scale));

        var size = VisionLlmImageDownscaler.DecodeSize(codec, scale, target.Item1, target.Item2);
        using var bitmap = VisionLlmImageDownscaler.Decode(codec, size);

        (bitmap!.Width, bitmap.Height).ShouldBe((width, height));
    }

    [Fact]
    public void A_webp_is_decoded_at_a_reduced_size()
    {
        using var codec = Codec(Encode(Solid(4000, 3000, SKColors.Gray), SKEncodedImageFormat.Webp));

        var size = VisionLlmImageDownscaler.DecodeSize(codec, 0.25, 1000, 750);
        using var bitmap = VisionLlmImageDownscaler.Decode(codec, size);

        (bitmap!.Width, bitmap.Height).ShouldBe((1000, 750));
    }

    [Fact]
    public void A_png_is_decoded_at_full_size()
    {
        using var codec = Codec(Encode(Solid(4000, 3000, SKColors.Gray), SKEncodedImageFormat.Png));

        var size = VisionLlmImageDownscaler.DecodeSize(codec, 0.25, 1000, 750);

        (size.Width, size.Height).ShouldBe((4000, 3000));
    }

    private static SKCodec Codec(byte[] encoded) => SKCodec.Create(new MemoryStream(encoded));

    private static bool IsRed(SKColor color) => color.Red > 180 && color.Blue < 80;

    private static bool IsBlue(SKColor color) => color.Blue > 180 && color.Red < 80;

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    private static SKBitmap LeftRedRightBlue(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);
        using var red = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(0, 0, width / 2f, height, red);
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(format, 95))
        {
            return data.ToArray();
        }
    }

    private static (int Width, int Height) ReadSize(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded));
        return (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>Inserts an EXIF block that carries only the orientation right after the JPEG's start marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        var exif = new List<byte>();
        exif.AddRange(new byte[] { 0xFF, 0xE1 });                 // APP1 marker
        exif.AddRange(new byte[] { 0x00, 0x22 });                 // segment length: 34 including these two bytes
        exif.AddRange("Exif\0\0"u8.ToArray());
        exif.AddRange(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08 }); // big-endian TIFF header, IFD at 8
        exif.AddRange(new byte[] { 0x00, 0x01 });                 // one entry
        exif.AddRange(new byte[] { 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00 });
        exif.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });     // no next IFD

        var result = new List<byte>(jpeg.Length + exif.Count);
        result.AddRange(jpeg.AsSpan(0, 2).ToArray());             // FF D8
        result.AddRange(exif);
        result.AddRange(jpeg.AsSpan(2).ToArray());
        return result.ToArray();
    }
}
