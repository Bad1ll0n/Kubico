using System.Net;
using System.Net.Http.Json;
using System.Text;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Revisão — fase 3A: limites das fotos de prova (SEC-07, QA-09, ENG-19).</summary>
public class PhotoLimitsTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Pictures_bigger_than_4096_pixels_or_without_a_frame_are_refused()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        byte[] noFrame = [0xFF, 0xD8, 0xFF, 0xD9];

        var wide = await PhotoProofTests.UploadRaw(admin, household, PhotoProofTests.JpegOf(5000, 10));
        var huge = await PhotoProofTests.UploadRaw(admin, household, Png(20000, 20000));
        var empty = await PhotoProofTests.UploadRaw(admin, household, noFrame);
        var fine = await PhotoProofTests.UploadRaw(admin, household, PhotoProofTests.JpegOf(4096, 3072));

        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        Assert.Equal("A foto é grande demais (no máximo 4096 × 4096 píxeis).",
            (await wide.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options))!.Title);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task Each_person_can_only_keep_a_few_unused_photos()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (guest, _) = await Fase1.MemberAsync(factory, admin, household, "Ama", HouseholdRole.Guest);

        for (var i = 0; i < ChorePhoto.MaxUnusedPerPerson; i++)
        {
            await PhotoProofTests.Upload(guest, household, PhotoProofTests.Jpeg);
        }

        var tooMany = await PhotoProofTests.UploadRaw(guest, household, PhotoProofTests.Jpeg);
        var adminStillCan = await PhotoProofTests.UploadRaw(admin, household, PhotoProofTests.Jpeg);

        Assert.Equal(HttpStatusCode.TooManyRequests, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.Created, adminStillCan.StatusCode);
    }

    [Fact]
    public void The_size_is_read_from_the_header_of_each_format()
    {
        byte[] vp8x = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 0x7F, 0x07, 0, 0x37, 0x04, 0];

        Assert.Equal((1920, 1080), PhotoMetadata.ReadSize(PhotoProofTests.JpegOf(1920, 1080), "image/jpeg"));
        Assert.Equal((20000, 20000), PhotoMetadata.ReadSize(Png(20000, 20000), "image/png"));
        Assert.Equal((1920, 1080), PhotoMetadata.ReadSize(vp8x, "image/webp"));
        Assert.Null(PhotoMetadata.ReadSize([0xFF, 0xD8, 0xFF, 0xD9], "image/jpeg"));
    }

    /// <summary>Um PNG só com o cabeçalho (IHDR) com estas dimensões, a imagem (IDAT) e o fim.</summary>
    private static byte[] Png(int width, int height)
    {
        static byte[] Chunk(string type, byte[] data) =>
            [.. BitConverter.GetBytes(data.Length).Reverse(), .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];
        byte[] header = [.. BitConverter.GetBytes(width).Reverse(), .. BitConverter.GetBytes(height).Reverse(), 8, 2, 0, 0, 0];
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("IDAT", [1, 2, 3]), .. Chunk("IEND", [])];
    }
}
