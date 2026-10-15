namespace Kubico.Domain.Calendar;

public sealed record Holiday(DateOnly Date, string Name);

/// <summary>Os 13 feriados nacionais obrigatórios de Portugal. O Carnaval e os feriados municipais não contam.</summary>
public static class PortugueseHolidays
{
    public static IEnumerable<Holiday> For(int year)
    {
        var easter = EasterSunday(year);
        Holiday[] holidays =
        [
            new(new DateOnly(year, 1, 1), "Ano Novo"),
            new(easter.AddDays(-2), "Sexta-feira Santa"),
            new(easter, "Páscoa"),
            new(new DateOnly(year, 4, 25), "Dia da Liberdade"),
            new(new DateOnly(year, 5, 1), "Dia do Trabalhador"),
            new(easter.AddDays(60), "Corpo de Deus"),
            new(new DateOnly(year, 6, 10), "Dia de Portugal"),
            new(new DateOnly(year, 8, 15), "Assunção de Nossa Senhora"),
            new(new DateOnly(year, 10, 5), "Implantação da República"),
            new(new DateOnly(year, 11, 1), "Todos os Santos"),
            new(new DateOnly(year, 12, 1), "Restauração da Independência"),
            new(new DateOnly(year, 12, 8), "Imaculada Conceição"),
            new(new DateOnly(year, 12, 25), "Natal"),
        ];
        return holidays.OrderBy(h => h.Date);
    }

    /// <summary>Feriados entre duas datas, inclusive.</summary>
    public static IEnumerable<Holiday> Between(DateOnly from, DateOnly to) =>
        Enumerable.Range(from.Year, to.Year - from.Year + 1)
            .SelectMany(For)
            .Where(h => h.Date >= from && h.Date <= to);

    /// <summary>Domingo de Páscoa no calendário gregoriano (algoritmo de Meeus/Jones/Butcher).</summary>
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100;
        int d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        var n = h + l - 7 * m + 114;
        return new DateOnly(year, n / 31, n % 31 + 1);
    }
}
