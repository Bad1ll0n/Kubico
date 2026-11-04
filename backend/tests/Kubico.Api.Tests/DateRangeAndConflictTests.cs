using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Revisão — fase 3A: datas fora do intervalo e linhas repetidas nunca dão 500 (QA-01, QA-05, ENG-04).</summary>
public class DateRangeAndConflictTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Theory]
    [InlineData("0001-01-01", "Daily")]
    [InlineData("0001-01-01", "Weekly")]
    [InlineData("0001-01-01", "Monthly")]
    [InlineData("1899-12-31", "None")]
    [InlineData("9999-12-31", "Daily")]
    public async Task A_chore_due_outside_1900_to_2200_is_refused_and_the_list_keeps_working(string dueDate, string recurrence)
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var chores = $"/api/households/{household}/chores";

        var created = await admin.PostAsJsonAsync(chores, new SaveChoreRequest(
            "Fazer a cama", DueDate: DateOnly.Parse(dueDate), Recurrence: Enum.Parse<ChoreRecurrence>(recurrence)), TestJson.Options);
        var problem = await created.Content.ReadFromJsonAsync<ValidationProblemDetails>(TestJson.Options);
        var list = await admin.GetAsync(chores);

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal("As datas têm de estar entre 1900 e 2200.", Assert.Single(problem!.Errors["DueDate"]));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task An_absence_at_the_end_of_the_calendar_is_refused()
    {
        var (admin, adminAuth, household) = await Fase1.HouseholdAsync(factory);

        var response = await admin.PostAsJsonAsync($"/api/households/{household}/absences",
            new SaveAbsenceRequest(adminAuth.User.Id, new DateOnly(9999, 12, 30), new DateOnly(9999, 12, 31)), TestJson.Options);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("As datas têm de estar entre 1900 e 2200.", Assert.Single(problem!.Errors["EndDate"]));
    }

    [Fact]
    public async Task Paying_an_allowance_period_in_year_one_is_refused()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, rita, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var url = $"/api/households/{household}/allowances";
        (await admin.PutAsJsonAsync($"{url}/{rita}", new SaveAllowanceRequest(500, 50), TestJson.Options)).EnsureSuccessStatusCode();

        var response = await admin.PostAsJsonAsync($"{url}/{rita}/payments", new PayAllowanceRequest(DateOnly.MinValue), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Two_sign_ups_with_the_same_email_at_the_same_time_never_give_500()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";

        var statuses = await Fase1.ParallelAsync(6, _ => factory.CreateClient().PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(email, "palavra-passe-segura", "Ana"), TestJson.Options));

        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statuses);
        Assert.Single(statuses, s => s is HttpStatusCode.OK or HttpStatusCode.Created);
    }

    [Fact]
    public void The_recurrence_maths_stops_at_the_ends_of_the_calendar_instead_of_throwing()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new Chore { Title = "x", Recurrence = ChoreRecurrence.Weekly, DueDate = DateOnly.MinValue };
        var last = new Chore { Title = "x", Recurrence = ChoreRecurrence.Daily, DueDate = DateOnly.MaxValue, AssigneeUserId = Guid.NewGuid() };
        var absence = new MemberAbsence
        {
            UserId = last.AssigneeUserId!.Value, StartDate = DateOnly.MaxValue.AddDays(-1), EndDate = DateOnly.MaxValue,
        };

        var availableFrom = first.AvailableFrom;
        last.PauseFor(absence, DateOnly.MaxValue.AddDays(-1), now);

        Assert.Equal(DateOnly.MinValue.AddDays(1), availableFrom);
        Assert.Equal(DateOnly.MaxValue, last.DueDate);
    }
}
