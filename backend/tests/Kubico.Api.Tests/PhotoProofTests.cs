using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Rewards;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Fotos de prova: a tarefa só se conclui com a foto, e quem gere as tarefas pode não aceitar.</summary>
public class PhotoProofTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    /// <summary>Um "JPEG" pequeno: basta o início certo para o servidor o reconhecer.</summary>
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8, 0x00, 0x01, 0x02, 0x03];

    [Fact]
    public async Task A_chore_that_needs_a_photo_is_completed_with_one_and_the_adults_can_see_it()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        var chores = $"/api/households/{household.Id}/chores";
        var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Arrumar o quarto", Points: 5, AssigneeUserId: childAuth.User.Id, RequiresPhoto: true));

        var withoutPhoto = await child.PostAsJsonAsync($"{chores}/{chore.Id}/complete", new CompleteChoreRequest(), TestJson.Options);
        var problem = await withoutPhoto.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        var photo = await Upload(child, household.Id, Jpeg);
        var done = await NotificationTests.Post<ChoreCompletedDto>(child, $"{chores}/{chore.Id}/complete", new CompleteChoreRequest(photo.Id));
        var mumInbox = await NotificationTests.Inbox(admin);
        var history = await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history", TestJson.Options);
        var image = await admin.GetAsync($"{chores}/photos/{photo.Id}");
        var strangerImage = await stranger.GetAsync($"{chores}/photos/{photo.Id}");

        Assert.True(chore.RequiresPhoto);
        Assert.Equal(HttpStatusCode.BadRequest, withoutPhoto.StatusCode);
        Assert.Equal("Esta tarefa precisa de uma foto do trabalho feito.", problem!.Title);
        Assert.Equal((5, true), (done.PointsEarned, done.Chore.IsDone));
        Assert.Contains(mumInbox.Items, n => n.Title == "Rita fez: Arrumar o quarto");
        var item = Assert.Single(history!);
        Assert.Equal((chore.Id, childAuth.User.Id, photo.Id, false), (item.ChoreId, item.UserId, item.PhotoId, item.Rejected));
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Jpeg, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, strangerImage.StatusCode);
        Assert.NotEqual(adminAuth.User.Id, item.UserId);
    }

    [Fact]
    public async Task Only_real_pictures_count_and_each_photo_proves_a_single_chore_of_whoever_sent_it()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        var chores = $"/api/households/{household.Id}/chores";
        var first = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Lavar a loiça", RequiresPhoto: true));
        var second = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Varrer", RequiresPhoto: true));

        var text = await UploadRaw(child, household.Id, "olá, não sou uma foto"u8.ToArray());
        var huge = await UploadRaw(child, household.Id, [.. Jpeg, .. new byte[5 * 1024 * 1024]]);
        var mumsPhoto = await Upload(admin, household.Id, Jpeg);
        var withMumsPhoto = await child.PostAsJsonAsync($"{chores}/{first.Id}/complete", new CompleteChoreRequest(mumsPhoto.Id), TestJson.Options);
        var photo = await Upload(child, household.Id, Jpeg);
        await NotificationTests.Post<ChoreCompletedDto>(child, $"{chores}/{first.Id}/complete", new CompleteChoreRequest(photo.Id));
        var reused = await child.PostAsJsonAsync($"{chores}/{second.Id}/complete", new CompleteChoreRequest(photo.Id), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withMumsPhoto.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.NotEqual(adminAuth.User.Id, childAuth.User.Id);
    }

    [Fact]
    public async Task A_rejected_chore_goes_back_to_whoever_did_it_and_its_points_stop_counting()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        var chores = $"/api/households/{household.Id}/chores";
        var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Arrumar o quarto", Points: 5, AssigneeUserId: childAuth.User.Id, RequiresPhoto: true));
        var photo = await Upload(child, household.Id, Jpeg);
        await NotificationTests.Post<ChoreCompletedDto>(child, $"{chores}/{chore.Id}/complete", new CompleteChoreRequest(photo.Id));
        var completion = (await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history", TestJson.Options))!.Single();

        var byChild = await child.PostAsync($"{chores}/completions/{completion.Id}/reject", null);
        var rejected = await NotificationTests.Post<ChoreHistoryItemDto>(admin, $"{chores}/completions/{completion.Id}/reject", null);
        var again = await admin.PostAsync($"{chores}/completions/{completion.Id}/reject", null);
        var open = await child.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options);
        var childInbox = await NotificationTests.Inbox(child);
        var rewards = await child.GetFromJsonAsync<RewardsDto>($"/api/households/{household.Id}/rewards", TestJson.Options);
        var stats = await child.GetFromJsonAsync<ChoreStatsDto>($"{chores}/stats", TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, byChild.StatusCode);
        Assert.True(rejected.Rejected);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var reopened = Assert.Single(open!);
        Assert.Equal((chore.Id, childAuth.User.Id, factory.Today), (reopened.Id, reopened.AssigneeUserId!.Value, reopened.DueDate!.Value));
        Assert.Contains(childInbox.Items, n => n.Title == "Faz outra vez: Arrumar o quarto");
        Assert.Equal(0, rewards!.Points.Single(p => p.UserId == childAuth.User.Id).Earned);
        Assert.Equal((0, 0), stats!.Members.Where(m => m.UserId == childAuth.User.Id).Select(m => (m.Completed, m.Points)).Single());
    }

    [Fact]
    public async Task A_routine_with_a_photo_waits_for_the_photo_after_the_last_step()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var chores = $"/api/households/{household.Id}/chores";
        var routine = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Arrumar a mochila", Points: 2, Recurrence: ChoreRecurrence.Daily, DueDate: factory.Today,
            Steps: ["Livros", "Estojo"], RequiresPhoto: true));

        await NotificationTests.Post<ChoreStepResultDto>(admin, $"{chores}/{routine.Id}/steps/0", new SetChoreStepRequest(true));
        var last = await NotificationTests.Post<ChoreStepResultDto>(admin, $"{chores}/{routine.Id}/steps/1", new SetChoreStepRequest(true));
        var photo = await Upload(admin, household.Id, Jpeg);
        var done = await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{routine.Id}/complete", new CompleteChoreRequest(photo.Id));

        Assert.False(last.Completed);
        Assert.Equal([0, 1], last.Chore.DoneSteps);
        Assert.Equal((2, factory.Today.AddDays(1)), (done.PointsEarned, done.Chore.DueDate!.Value));
    }

    internal static async Task<ChorePhotoDto> Upload(HttpClient client, Guid householdId, byte[] data)
    {
        var response = await UploadRaw(client, householdId, data);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChorePhotoDto>(TestJson.Options))!;
    }

    internal static Task<HttpResponseMessage> UploadRaw(HttpClient client, Guid householdId, byte[] data)
    {
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var form = new MultipartFormDataContent { { file, "photo", "foto.jpg" } };
        return client.PostAsync($"/api/households/{householdId}/chores/photos", form);
    }
}

/// <summary>Fotos antigas apagam-se. Servidor próprio: o teste avança o relógio.</summary>
public sealed class PhotoCleanupTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task Unused_photos_go_after_a_day_and_the_others_after_ninety_days_keeping_the_history()
    {
        var (admin, _) = await _factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var chores = $"/api/households/{household.Id}/chores";
        var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Arrumar", RequiresPhoto: true));
        var used = await PhotoProofTests.Upload(admin, household.Id, PhotoProofTests.Jpeg);
        await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{chore.Id}/complete", new CompleteChoreRequest(used.Id));
        var unused = await PhotoProofTests.Upload(admin, household.Id, PhotoProofTests.Jpeg);

        _factory.Clock.Advance(TimeSpan.FromDays(2));
        await PhotoProofTests.Upload(admin, household.Id, PhotoProofTests.Jpeg);
        var unusedAfterTwoDays = await admin.GetAsync($"{chores}/photos/{unused.Id}");
        var usedAfterTwoDays = await admin.GetAsync($"{chores}/photos/{used.Id}");

        _factory.Clock.Advance(TimeSpan.FromDays(90));
        await PhotoProofTests.Upload(admin, household.Id, PhotoProofTests.Jpeg);
        var usedAfterThreeMonths = await admin.GetAsync($"{chores}/photos/{used.Id}");
        var history = await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history?days=365", TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, unusedAfterTwoDays.StatusCode);
        Assert.Equal(HttpStatusCode.OK, usedAfterTwoDays.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, usedAfterThreeMonths.StatusCode);
        Assert.Null(Assert.Single(history!).PhotoId);
    }

    public void Dispose() => _factory.Dispose();
}
