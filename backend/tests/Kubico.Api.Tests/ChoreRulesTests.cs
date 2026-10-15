using Kubico.Domain.Chores;

namespace Kubico.Api.Tests;

/// <summary>Regras de recorrência e rotação, sem base de dados.</summary>
public class ChoreRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    [Theory]
    [InlineData(ChoreRecurrence.Daily, "2026-10-15", "2026-10-16")]
    [InlineData(ChoreRecurrence.Weekly, "2026-10-15", "2026-10-22")]
    [InlineData(ChoreRecurrence.Monthly, "2026-10-15", "2026-11-15")]
    [InlineData(ChoreRecurrence.Monthly, "2026-01-31", "2026-10-28")]
    public void Next_due_date_is_after_today(ChoreRecurrence recurrence, string from, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), Chore.NextDueDate(DateOnly.Parse(from), recurrence, Today));
    }

    [Fact]
    public void Overdue_weekly_chore_skips_the_weeks_that_already_passed()
    {
        // Prazo era dia 1; concluída a dia 15 → próximo prazo dia 22, não dia 8 nem 15.
        Assert.Equal(new DateOnly(2026, 10, 22), Chore.NextDueDate(new DateOnly(2026, 10, 1), ChoreRecurrence.Weekly, Today));
    }

    [Fact]
    public void Completing_a_rotating_chore_passes_it_to_the_next_member_and_wraps_around()
    {
        Guid ana = Guid.NewGuid(), rui = Guid.NewGuid(), eva = Guid.NewGuid();
        var chore = new Chore
        {
            Title = "Lixo",
            Points = 5,
            Recurrence = ChoreRecurrence.Weekly,
            DueDate = Today,
            RotationUserIds = [ana, rui, eva],
            AssigneeUserId = eva,
        };

        var completion = chore.Complete(eva, DateTimeOffset.UtcNow, Today);

        Assert.Equal(ana, chore.AssigneeUserId);
        Assert.Equal(Today.AddDays(7), chore.DueDate);
        Assert.Equal(5, completion.Points);
        Assert.False(chore.IsDone);
    }

    [Fact]
    public void One_off_chore_is_done_after_completion()
    {
        var chore = new Chore { Title = "Arranjar torneira", DueDate = Today };

        chore.Complete(Guid.NewGuid(), DateTimeOffset.UtcNow, Today);

        Assert.True(chore.IsDone);
        Assert.Equal(Today, chore.DueDate);
    }
}
