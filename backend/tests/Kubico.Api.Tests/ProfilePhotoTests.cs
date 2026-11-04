using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kubico.Api.Auth;
using Kubico.Api.Households;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>A fotografia de perfil (F4-03): só adultos com conta, sem metadados, vista só por quem partilha uma casa.</summary>
public class ProfilePhotoTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task A_profile_photo_is_seen_by_the_household_only_and_follows_the_member_everywhere()
    {
        var (mum, auth, household) = await Fase1.HouseholdAsync(factory);
        var (dad, _) = await Fase1.MemberAsync(factory, mum, household, "Pai", HouseholdRole.Adult);
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");

        var me = await Upload(mum, PhotoProofTests.Jpeg);
        var url = $"/api/users/{auth.User.Id}/photo";
        var asDad = await dad.GetAsync(url);
        var asStranger = await stranger.GetAsync(url);
        using var again = new HttpRequestMessage(HttpMethod.Get, url);
        again.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(asDad.Headers.ETag!.ToString()));
        var unchanged = await dad.SendAsync(again);
        var details = await Fase1.HouseholdDetailsAsync(dad, household);
        var exported = JsonDocument.Parse(await (await mum.GetAsync("/api/account/export")).Content.ReadAsStringAsync());

        Assert.NotNull(me.Photo);
        Assert.Equal(HttpStatusCode.OK, asDad.StatusCode);
        Assert.Equal("image/jpeg", asDad.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, asStranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(me.Photo, details.Members.Single(m => m.UserId == auth.User.Id).Photo);
        Assert.Equal("image/jpeg", exported.RootElement.GetProperty("Conta").GetProperty("Fotografia").GetProperty("Tipo").GetString());

        // Uma fotografia nova muda a versão; tirá-la deixa a inicial (ou o emoji).
        var second = await Upload(mum, PhotoProofTests.JpegOf(32, 32));
        Assert.NotEqual(me.Photo, second.Photo);
        (await mum.DeleteAsync("/api/account/photo")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await dad.GetAsync(url)).StatusCode);
        Assert.Null((await Fase1.HouseholdDetailsAsync(dad, household)).Members.Single(m => m.UserId == auth.User.Id).Photo);
    }

    [Fact]
    public async Task Children_keep_their_emoji_and_files_that_are_not_photos_are_refused()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, mum, household, "Rita");

        var childTries = await Send(child, PhotoProofTests.Jpeg);
        var notAPhoto = await Send(mum, "olá, não sou uma foto"u8.ToArray());
        var huge = await Send(mum, PhotoProofTests.JpegOf(5000, 100));

        Assert.Equal(HttpStatusCode.Forbidden, childTries.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAPhoto.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
    }

    private static async Task<UserDto> Upload(HttpClient client, byte[] photo)
    {
        var response = await Send(client, photo);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserDto>(TestJson.Options))!;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, byte[] photo)
    {
        var content = new MultipartFormDataContent { { new ByteArrayContent(photo), "photo", "eu.jpg" } };
        return client.PutAsync("/api/account/photo", content);
    }
}
