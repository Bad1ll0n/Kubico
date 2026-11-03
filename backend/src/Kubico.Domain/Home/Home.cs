namespace Kubico.Domain.Home;

public enum ApplianceKind
{
    General = 0,

    /// <summary>Um carro (ligeiro de passageiros): tem matrícula, IUC e inspeção (IPO).</summary>
    Car = 1,
}

/// <summary>Um equipamento da casa (F4-07): garantia, manutenção periódica e ficheiros (fatura, manual).</summary>
public class Appliance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public ApplianceKind Kind { get; set; }
    public string? Room { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public DateOnly? PurchasedOn { get; set; }
    public DateOnly? WarrantyUntil { get; set; }

    /// <summary>Num carro: a matrícula e a data da primeira matrícula (dela saem o IUC e a inspeção).</summary>
    public string? Plate { get; set; }

    public DateOnly? FirstRegistration { get; set; }
    public string? Notes { get; set; }
    public List<MaintenanceTask> Maintenance { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Uma manutenção que se repete ("Limpar o filtro do exaustor, a cada 90 dias").</summary>
public class MaintenanceTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public int IntervalDays { get; set; }
    public DateOnly? LastDone { get; set; }
    public DateOnly NextDue { get; set; }
}

public enum DocumentKind
{
    CitizenCard = 0,
    Passport = 1,
    DrivingLicence = 2,
    Vaccination = 3,
    Insurance = 4,
    Other = 5,
}

/// <summary>
/// Um documento do cofre (F4-13): o tipo, de quem é, a validade e o número (cifrado com o Data Protection; só o
/// veem a própria pessoa e quem gere a área Casa).
/// </summary>
public class HomeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public DocumentKind Kind { get; set; }
    public required string Name { get; set; }

    /// <summary>De quem é (null: da casa, como um seguro da casa).</summary>
    public Guid? OwnerUserId { get; set; }

    public string? ProtectedNumber { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Um ficheiro de um equipamento ou documento (PDF ou imagem), guardado na base de dados.</summary>
public class HomeFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }

    /// <summary>O equipamento ou o documento a que pertence.</summary>
    public Guid OwnerId { get; set; }

    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Content { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Um recado no quadro da casa (F4-12): aparece no Hoje e no painel de cozinha até à validade.</summary>
public class HouseNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public required string Text { get; set; }
    public DateOnly? Until { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Um contacto útil (médico, escola, canalizador), com o aniversário opcional.</summary>
public class HouseContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public string? Role { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }

    /// <summary>O dia de anos (o ano não interessa; guarda-se num ano bissexto, para o 29 de fevereiro).</summary>
    public DateOnly? Birthday { get; set; }
}

/// <summary>As regras de Portugal (F4-08): IUC, inspeção periódica obrigatória (IPO) e IRS.</summary>
public static class PortugalCalendar
{
    /// <summary>
    /// O IUC paga-se no mês da primeira matrícula, todos os anos: o primeiro dia desse mês a partir de
    /// <paramref name="today"/> (o deste ano, se ainda não passou o mês).
    /// </summary>
    public static DateOnly NextIuc(DateOnly firstRegistration, DateOnly today)
    {
        var thisYear = new DateOnly(today.Year, firstRegistration.Month, 1);
        return thisYear.AddMonths(1) > today ? thisYear : thisYear.AddYears(1);
    }

    /// <summary>
    /// A próxima inspeção de um ligeiro de passageiros: aos 4 anos da matrícula, depois de 2 em 2 anos até aos 8 e
    /// depois todos os anos, sempre até ao dia e mês da matrícula. Devolve a primeira data a partir de hoje.
    /// </summary>
    public static DateOnly NextInspection(DateOnly firstRegistration, DateOnly today)
    {
        var due = firstRegistration.AddYears(4);
        while (due < today)
        {
            var age = due.Year - firstRegistration.Year;
            due = due.AddYears(age < 8 ? 2 : 1);
        }

        return due;
    }

    /// <summary>Os avisos do IRS: validar as faturas no e-Fatura (fevereiro) e a entrega (1 de abril a 30 de junho).</summary>
    public static IEnumerable<(DateOnly Date, string Title)> IrsDates(int year) =>
    [
        (new DateOnly(year, 2, 1), "IRS: valida as faturas no e-Fatura até 25 de fevereiro"),
        (new DateOnly(year, 4, 1), "IRS: a entrega da declaração começa hoje (até 30 de junho)"),
        (new DateOnly(year, 6, 15), "IRS: faltam 15 dias para acabar o prazo de entrega"),
    ];
}
