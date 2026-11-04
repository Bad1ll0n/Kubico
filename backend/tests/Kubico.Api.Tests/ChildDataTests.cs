using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>Revisão — fase 3A: os dados das crianças (calendário subscrito, PIN, fotos de prova).</summary>
public class ChildDataTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task A_child_profile_cannot_get_a_calendar_address_that_works_outside_the_app()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");

        var response = await child.PostAsync($"/api/households/{household}/calendar-feed", null);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Os perfis de criança não podem subscrever o calendário fora da app.", problem!.Title);
    }

    [Fact]
    public async Task Changing_the_pin_or_renewing_the_family_code_also_kills_a_calendar_address_the_child_had()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, bruno, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Bruno");
        var (_, carla, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Carla");
        // Endereços criados antes desta versão (as crianças já não os conseguem pedir).
        var brunoFeed = await GiveFeedAsync(household, bruno);
        var carlaFeed = await GiveFeedAsync(household, carla);
        var anonymous = factory.CreateClient();
        var brunoBefore = await anonymous.GetAsync(brunoFeed);

        (await admin.PutAsJsonAsync($"/api/households/{household}/children/{bruno}/pin", new ChildPinRequest("5938"), TestJson.Options))
            .EnsureSuccessStatusCode();
        var brunoAfterPin = await anonymous.GetAsync(brunoFeed);
        var carlaAfterBrunoPin = await anonymous.GetAsync(carlaFeed);
        (await admin.DeleteAsync($"/api/households/{household}/family-code")).EnsureSuccessStatusCode();
        var carlaAfterCode = await anonymous.GetAsync(carlaFeed);

        Assert.Equal(HttpStatusCode.OK, brunoBefore.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, brunoAfterPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, carlaAfterBrunoPin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, carlaAfterCode.StatusCode);
    }

    [Fact]
    public async Task Signing_out_everywhere_kills_the_calendar_addresses_unless_asked_to_keep_them()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var anonymous = factory.CreateClient();
        var first = await Feed(admin, household);

        (await admin.PostAsJsonAsync("/api/auth/logout-all", new LogoutAllRequest(ResetCalendarFeeds: false), TestJson.Options))
            .EnsureSuccessStatusCode();
        var kept = await anonymous.GetAsync(first.Url);

        var (other, _, otherHousehold) = await Fase1.HouseholdAsync(factory);
        var second = await Feed(other, otherHousehold);
        (await other.PostAsync("/api/auth/logout-all", null)).EnsureSuccessStatusCode();
        var gone = await anonymous.GetAsync(second.Url);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("0000")]
    [InlineData("4321")]
    [InlineData("2015")]
    [InlineData("1987")]
    [InlineData("987654")]
    [InlineData("111111")]
    public async Task Pins_that_are_the_first_ones_anyone_would_try_are_refused(string pin)
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);

        var response = await admin.PostAsJsonAsync(
            $"/api/households/{household}/children", new CreateChildProfileRequest("Rita", pin), TestJson.Options);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Escolhe um PIN mais difícil de adivinhar (não uses algarismos repetidos, seguidos nem um ano).",
            Assert.Single(problem!.Errors["Pin"]));
    }

    [Theory]
    [InlineData("4829", false)]
    [InlineData("1357", false)]
    [InlineData("2100", false)]
    [InlineData("1235", false)]
    [InlineData("5678", true)]
    [InlineData("2099", true)]
    public void Easy_pins_are_repeated_or_consecutive_digits_or_a_year(string pin, bool easy) =>
        Assert.Equal(easy, ChildProfileEndpoints.IsEasyToGuess(pin));

    [Fact]
    public async Task Only_whoever_sent_a_photo_and_whoever_manages_the_chores_can_see_it()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (rita, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var (bruno, _, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Bruno");
        var (guest, _) = await Fase1.MemberAsync(factory, admin, household, "Ama", HouseholdRole.Guest);
        var chores = $"/api/households/{household}/chores";
        var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Arrumar o quarto", AssigneeUserId: ritaId, RequiresPhoto: true));
        var photo = await PhotoProofTests.Upload(rita, household, PhotoProofTests.Jpeg);
        await NotificationTests.Post<ChoreCompletedDto>(rita, $"{chores}/{chore.Id}/complete", new CompleteChoreRequest(photo.Id));

        async Task<(HttpStatusCode Image, Guid? InHistory)> Sees(HttpClient client)
        {
            var image = await client.GetAsync($"{chores}/photos/{photo.Id}");
            var history = await client.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history", TestJson.Options);
            return (image.StatusCode, Assert.Single(history!).PhotoId);
        }

        Assert.Equal((HttpStatusCode.OK, photo.Id), await Sees(admin));
        Assert.Equal((HttpStatusCode.OK, photo.Id), await Sees(rita));
        Assert.Equal((HttpStatusCode.NotFound, (Guid?)null), await Sees(bruno));
        Assert.Equal((HttpStatusCode.NotFound, (Guid?)null), await Sees(guest));
    }

    [Fact]
    public async Task Removing_a_child_profile_deletes_its_photos_right_away()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (rita, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var chores = $"/api/households/{household}/chores";
        var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Arrumar o quarto", AssigneeUserId: ritaId, RequiresPhoto: true));
        var photo = await PhotoProofTests.Upload(rita, household, PhotoProofTests.Jpeg);
        await NotificationTests.Post<ChoreCompletedDto>(rita, $"{chores}/{chore.Id}/complete", new CompleteChoreRequest(photo.Id));

        (await admin.DeleteAsync($"/api/households/{household}/members/{ritaId}")).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KubicoDbContext>();
        Assert.False(await db.ChorePhotos.AnyAsync(p => p.Id == photo.Id));
    }

    [Fact]
    public async Task Repeated_lockouts_last_longer_and_the_adults_are_told()
    {
        await using var own = new KubicoApiFactory();
        var (admin, _, household) = await Fase1.HouseholdAsync(own);
        var (adult, _) = await Fase1.MemberAsync(own, admin, household, "Pai", HouseholdRole.Adult);
        var (guest, _) = await Fase1.MemberAsync(own, admin, household, "Ama", HouseholdRole.Guest);
        var tiago = await ChildProfileTests.CreateChild(admin, household, "Tiago", "4829");
        var code = await ChildProfileTests.FamilyCode(admin, household);
        var anonymous = own.CreateClient();

        async Task<string?> LockOut()
        {
            HttpResponseMessage last = null!;
            for (var i = 0; i < 5; i++)
            {
                last = await anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, tiago.UserId, "0000"));
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
            return (await last.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options))!.Title;
        }

        Task<HttpResponseMessage> RightPin() =>
            anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, tiago.UserId, "4829"));

        var first = await LockOut();
        own.Clock.Advance(TimeSpan.FromMinutes(16));
        var second = await LockOut();
        own.Clock.Advance(TimeSpan.FromMinutes(16));
        var stillLocked = (await RightPin()).StatusCode;
        own.Clock.Advance(TimeSpan.FromMinutes(45));
        var third = await LockOut();
        own.Clock.Advance(TimeSpan.FromHours(23));
        var lockedForADay = (await RightPin()).StatusCode;
        own.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        var afterADay = (await RightPin()).StatusCode;
        var fourth = await LockOut();

        Assert.Equal("Demasiados PINs errados. Tenta outra vez daqui a 15 minutos.", first);
        Assert.Equal("Demasiados PINs errados. Tenta outra vez daqui a 60 minutos.", second);
        Assert.Equal(HttpStatusCode.TooManyRequests, stillLocked);
        Assert.Equal("Demasiados PINs errados. Tenta outra vez daqui a 24 horas ou pede a um adulto para mudar o PIN.", third);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedForADay);
        Assert.Equal(HttpStatusCode.OK, afterADay);
        Assert.Equal("Demasiados PINs errados. Tenta outra vez daqui a 15 minutos.", fourth);

        var warnings = (await NotificationTests.Inbox(adult)).Items.Where(n => n.Kind == NotificationKind.ChildPinLocked).ToList();
        Assert.Equal(4, warnings.Count);
        Assert.All(warnings, n => Assert.Equal("O perfil de Tiago ficou bloqueado por PINs errados", n.Title));
        Assert.Contains((await NotificationTests.Inbox(admin)).Items, n => n.Kind == NotificationKind.ChildPinLocked);
        Assert.DoesNotContain((await NotificationTests.Inbox(guest)).Items, n => n.Kind == NotificationKind.ChildPinLocked);
    }

    private static async Task<CalendarFeedDto> Feed(HttpClient client, Guid householdId)
    {
        var response = await client.PostAsync($"/api/households/{householdId}/calendar-feed", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CalendarFeedDto>(TestJson.Options))!;
    }

    /// <summary>Põe um endereço de calendário na criança diretamente na base (como os que existiam antes).</summary>
    private async Task<string> GiveFeedAsync(Guid householdId, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KubicoDbContext>();
        var token = Guid.NewGuid().ToString("N");
        await db.Memberships.Where(m => m.HouseholdId == householdId && m.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CalendarFeedToken, token));
        return $"/api/calendar-feeds/{token}.ics";
    }
}
