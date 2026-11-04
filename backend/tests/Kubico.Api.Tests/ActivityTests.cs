using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Activity;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Histórico: quem mudou o quê, e desfazer (só a última alteração a cada coisa).</summary>
public class ActivityTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Shopping_changes_show_who_did_them_and_undo_one_step_at_a_time()
    {
        var (mum, mumAuth) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(mum, "Casa");
        var list = await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household.Id}/lists", new CreateShoppingListRequest("Supermercado"));
        var items = $"/api/households/{household.Id}/lists/{list.Id}/items";
        var milk = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Leite"));
        (await mum.PatchAsJsonAsync($"{items}/{milk.Id}", new UpdateShoppingItemRequest(IsChecked: true), TestJson.Options)).EnsureSuccessStatusCode();

        var history = await History(mum, household.Id);
        var added = history.Single(a => a.Summary == "pôs «Leite» na lista «Supermercado»");
        var checkedOff = history.Single(a => a.Summary == "marcou «Leite» como comprado");
        var tooEarly = await mum.PostAsync(Undo(household.Id, added.Id), null);
        var undoneCheck = await mum.PostAsync(Undo(household.Id, checkedOff.Id), null);
        var afterUndo = await mum.GetFromJsonAsync<List<ShoppingItemDto>>(items, TestJson.Options);
        var undoneAdd = await mum.PostAsync(Undo(household.Id, added.Id), null);
        var twice = await mum.PostAsync(Undo(household.Id, added.Id), null);
        var empty = await mum.GetFromJsonAsync<List<ShoppingItemDto>>(items, TestJson.Options);
        var historyAfter = await History(mum, household.Id);

        Assert.Equal((mumAuth.User.Id, HouseholdModule.Shopping), (added.UserId, added.Module!.Value));
        Assert.False(added.CanUndo);
        Assert.True(checkedOff.CanUndo);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, undoneCheck.StatusCode);
        Assert.False(Assert.Single(afterUndo!).IsChecked);
        Assert.Equal(HttpStatusCode.OK, undoneAdd.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Empty(empty!);
        Assert.Contains(historyAfter, a => a.IsUndo && a.Summary == "desfez: pôs «Leite» na lista «Supermercado»");
        Assert.NotNull(historyAfter.Single(a => a.Id == added.Id).UndoneAt);
    }

    [Fact]
    public async Task A_deleted_list_comes_back_with_its_items()
    {
        var (mum, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(mum, "Casa");
        var list = await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household.Id}/lists", new CreateShoppingListRequest("Farmácia"));
        var items = $"/api/households/{household.Id}/lists/{list.Id}/items";
        var first = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Pensos"));
        await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Ben-u-ron", Quantity: "2"));
        (await mum.DeleteAsync($"/api/households/{household.Id}/lists/{list.Id}")).EnsureSuccessStatusCode();

        var deleted = (await History(mum, household.Id)).Single(a => a.Summary == "apagou a lista «Farmácia»");
        var undone = await mum.PostAsync(Undo(household.Id, deleted.Id), null);
        var created = (await History(mum, household.Id)).Single(a => a.Summary == "criou a lista «Farmácia»");
        var lists = await mum.GetFromJsonAsync<List<ShoppingListDto>>($"/api/households/{household.Id}/lists", TestJson.Options);
        var back = await mum.GetFromJsonAsync<List<ShoppingItemDto>>(items, TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.False(created.CanUndo, "a lista tem artigos postos depois de ser criada");
        Assert.Equal((list.Id, 2), (Assert.Single(lists!).Id, lists![0].ItemCount));
        Assert.Contains(back!, i => i.Id == first.Id);
        Assert.Contains(back!, i => i.Name == "Ben-u-ron" && i.Quantity == "2");
    }

    [Fact]
    public async Task Undoing_a_done_chore_takes_back_the_points_and_a_deleted_chore_returns_with_its_history()
    {
        var (mum, _) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(mum, "Casa");
        await NotificationTests.Join(mum, child, household.Id, HouseholdRole.Child);
        var chores = $"/api/households/{household.Id}/chores";
        var today = factory.Today;
        var bed = await NotificationTests.Post<ChoreDto>(mum, chores, new SaveChoreRequest(
            "Fazer a cama", Points: 2, DueDate: today, Recurrence: ChoreRecurrence.Daily, AssigneeUserId: childAuth.User.Id));
        await NotificationTests.Post<ChoreCompletedDto>(child, $"{chores}/{bed.Id}/complete", null);

        var done = (await History(child, household.Id)).Single(a => a.Summary == "fez «Fazer a cama»");
        var undone = await child.PostAsync(Undo(household.Id, done.Id), null);
        var afterUndo = (await child.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options))!.Single();
        var points = await child.GetFromJsonAsync<RewardsDto>($"/api/households/{household.Id}/rewards", TestJson.Options);

        await NotificationTests.Post<ChoreCompletedDto>(child, $"{chores}/{bed.Id}/complete", null);
        (await mum.DeleteAsync($"{chores}/{bed.Id}")).EnsureSuccessStatusCode();
        var deleted = (await History(mum, household.Id)).Single(a => a.Summary == "apagou a tarefa «Fazer a cama»");
        var restored = await mum.PostAsync(Undo(household.Id, deleted.Id), null);
        var stats = await mum.GetFromJsonAsync<ChoreStatsDto>($"{chores}/stats", TestJson.Options);

        Assert.True(done.CanUndo);
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Equal(today, afterUndo.DueDate);
        Assert.Equal(0, points!.Points.Single(p => p.UserId == childAuth.User.Id).Earned);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal((1, 2), stats!.Members.Where(m => m.UserId == childAuth.User.Id).Select(m => (m.Completed, m.Points)).Single());
    }

    [Fact]
    public async Task Events_come_back_with_their_exceptions_and_edits_go_back_to_what_they_were()
    {
        var (mum, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(mum, "Casa");
        var events = $"/api/households/{household.Id}/events";
        var monday = factory.Today.AddDays(1);
        var swimming = await NotificationTests.Post<CalendarEventDto>(mum, events, new SaveEventRequest(
            "Natação", IsAllDay: true, StartDate: monday, EndDate: monday, Recurrence: "FREQ=WEEKLY"));
        var moved = await mum.PutAsJsonAsync($"{events}/{swimming.Id}/occurrences/{monday.AddDays(7):yyyy-MM-dd}?scope=this",
            new SaveEventRequest("Natação (piscina nova)", IsAllDay: true, StartDate: monday.AddDays(8), EndDate: monday.AddDays(8)), TestJson.Options);
        (await mum.PutAsJsonAsync($"{events}/{swimming.Id}",
            new SaveEventRequest("Natação e ginástica", IsAllDay: true, StartDate: monday, EndDate: monday, Recurrence: "FREQ=WEEKLY"), TestJson.Options))
            .EnsureSuccessStatusCode();

        var renamed = (await History(mum, household.Id)).Single(a => a.Summary == "mudou o evento «Natação e ginástica»");
        await mum.PostAsync(Undo(household.Id, renamed.Id), null);
        var afterRename = await Calendar(mum, events, monday);
        (await mum.DeleteAsync($"{events}/{swimming.Id}")).EnsureSuccessStatusCode();
        var deleted = (await History(mum, household.Id)).Single(a => a.Summary == "apagou o evento «Natação»");
        var undone = await mum.PostAsync(Undo(household.Id, deleted.Id), null);
        var back = await Calendar(mum, events, monday);

        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);
        Assert.Contains("Natação", afterRename);
        Assert.DoesNotContain("Natação e ginástica", afterRename);
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Equal(["Natação", "Natação (piscina nova)", "Natação"], back);
    }

    [Fact]
    public async Task Each_one_undoes_their_own_changes_and_whoever_manages_the_area_undoes_anyones()
    {
        var (mum, _) = await factory.CreateUserAsync("Mãe");
        var (child, _) = await factory.CreateUserAsync("Rita");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(mum, "Casa");
        await NotificationTests.Join(mum, child, household.Id, HouseholdRole.Child);
        var list = await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household.Id}/lists", new CreateShoppingListRequest("Supermercado"));
        var items = $"/api/households/{household.Id}/lists/{list.Id}/items";
        await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Café"));
        await NotificationTests.Post<ShoppingItemDto>(child, items, new AddShoppingItemRequest("Gomas"));
        await NotificationTests.Post<RewardDto>(mum, $"/api/households/{household.Id}/rewards", new SaveRewardRequest("Cinema", Cost: 50));
        (await mum.PutAsJsonAsync($"/api/households/{household.Id}/permissions", new SavePermissionsRequest([
            PermissionTests.Role(HouseholdRole.Child, (HouseholdModule.Rewards, ModuleAccess.None)),
        ]), TestJson.Options)).EnsureSuccessStatusCode();

        var childSees = await History(child, household.Id);
        var mumsCoffee = childSees.Single(a => a.Summary == "pôs «Café» na lista «Supermercado»");
        var childsSweets = childSees.Single(a => a.Summary == "pôs «Gomas» na lista «Supermercado»");
        var childUndoesMum = await child.PostAsync(Undo(household.Id, mumsCoffee.Id), null);
        var mumUndoesChild = await mum.PostAsync(Undo(household.Id, childsSweets.Id), null);
        var strangerReads = await stranger.GetAsync($"/api/households/{household.Id}/activity");

        Assert.False(mumsCoffee.CanUndo, "a Rita não pode desfazer o que a Mãe fez");
        Assert.True(childsSweets.CanUndo);
        Assert.True((await History(mum, household.Id)).Single(a => a.Id == mumsCoffee.Id).CanUndo,
            "artigos diferentes da mesma lista desfazem-se cada um por si");
        Assert.DoesNotContain(childSees, a => a.Module == HouseholdModule.Rewards);
        Assert.Contains(childSees, a => a.Summary == "entrou na casa" && !a.CanUndo);
        Assert.Equal(HttpStatusCode.Forbidden, childUndoesMum.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mumUndoesChild.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerReads.StatusCode);
    }

    private static async Task<List<ActivityDto>> History(HttpClient client, Guid householdId) =>
        (await client.GetFromJsonAsync<List<ActivityDto>>($"/api/households/{householdId}/activity", TestJson.Options))!;

    private static string Undo(Guid householdId, long entryId) => $"/api/households/{householdId}/activity/{entryId}/undo";

    /// <summary>Os títulos das próximas três semanas, por ordem.</summary>
    private static async Task<List<string>> Calendar(HttpClient client, string events, DateOnly from)
    {
        var calendar = await client.GetFromJsonAsync<CalendarDto>(
            $"{events}?from={from:yyyy-MM-dd}&to={from.AddDays(15):yyyy-MM-dd}", TestJson.Options);
        return [.. calendar!.Occurrences.Select(o => calendar.Events.Single(e => e.Id == o.EventId).Title)];
    }
}
