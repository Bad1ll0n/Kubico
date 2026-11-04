using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;

namespace Kubico.Api.Tests;

/// <summary>Avatar com emoji (F4-03): adultos e crianças escolhem um só emoji; a casa vê-o.</summary>
public class AvatarTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task A_child_picks_an_emoji_the_household_sees_it_and_text_is_refused()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");

        var fox = await child.PutAsJsonAsync("/api/account/avatar", new AvatarRequest("🦊"), TestJson.Options);
        var family = await admin.PutAsJsonAsync("/api/account/avatar", new AvatarRequest("👩‍👧"), TestJson.Options);
        var word = await admin.PutAsJsonAsync("/api/account/avatar", new AvatarRequest("ola"), TestJson.Options);
        var two = await admin.PutAsJsonAsync("/api/account/avatar", new AvatarRequest("🦊🐶"), TestJson.Options);
        var members = (await Fase1.HouseholdDetailsAsync(admin, household)).Members;

        Assert.Equal(HttpStatusCode.NoContent, fox.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, family.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, word.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);
        Assert.Equal("🦊", members.Single(m => m.UserId == ritaId).Avatar);
    }
}
