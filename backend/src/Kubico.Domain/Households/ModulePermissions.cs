namespace Kubico.Domain.Households;

/// <summary>As áreas da app cujo acesso se escolhe por papel.</summary>
public enum HouseholdModule
{
    Shopping = 0,
    Chores = 1,
    Calendar = 2,
    Rewards = 3,
}

/// <summary>
/// O que um papel pode fazer numa área, do menos para o mais: não a vê; só vê; usa (adiciona e marca artigos,
/// conclui as suas tarefas, pede recompensas); gere tudo (cria, altera, apaga e aprova).
/// </summary>
public enum ModuleAccess
{
    None = 0,
    View = 1,
    Participate = 2,
    Manage = 3,
}

/// <summary>Uma escolha do administrador: o acesso de um papel a uma área. O que não está escolhido usa o habitual.</summary>
public class HouseholdPermission
{
    public Guid HouseholdId { get; set; }
    public HouseholdRole Role { get; set; }
    public HouseholdModule Module { get; set; }
    public ModuleAccess Access { get; set; }
}

public static class ModulePermissions
{
    public static readonly HouseholdModule[] Modules = Enum.GetValues<HouseholdModule>();

    /// <summary>Os papéis cujo acesso se escolhe (o administrador tem sempre tudo).</summary>
    public static readonly HouseholdRole[] ConfigurableRoles = [HouseholdRole.Adult, HouseholdRole.Child, HouseholdRole.Guest];

    /// <summary>O habitual: adultos gerem tudo; crianças e convidados usam, e no calendário só veem.</summary>
    public static ModuleAccess Default(HouseholdRole role, HouseholdModule module) => role switch
    {
        HouseholdRole.Admin or HouseholdRole.Adult => ModuleAccess.Manage,
        _ => module == HouseholdModule.Calendar ? ModuleAccess.View : ModuleAccess.Participate,
    };

    /// <summary>No calendário não há meio-termo entre ver e gerir.</summary>
    public static bool IsAllowed(HouseholdModule module, ModuleAccess access) =>
        Enum.IsDefined(access) && !(module == HouseholdModule.Calendar && access == ModuleAccess.Participate);

    public static ModuleAccess Resolve(HouseholdRole role, HouseholdModule module, IEnumerable<HouseholdPermission> chosen) =>
        role == HouseholdRole.Admin
            ? ModuleAccess.Manage
            : chosen.FirstOrDefault(p => p.Role == role && p.Module == module)?.Access ?? Default(role, module);

    /// <summary>O acesso de um papel a todas as áreas.</summary>
    public static Dictionary<HouseholdModule, ModuleAccess> ResolveAll(HouseholdRole role, IEnumerable<HouseholdPermission> chosen)
    {
        var list = chosen.ToList();
        return Modules.ToDictionary(m => m, m => Resolve(role, m, list));
    }

    /// <summary>O nome da área, para as mensagens.</summary>
    public static string Label(HouseholdModule module) => module switch
    {
        HouseholdModule.Shopping => "Compras",
        HouseholdModule.Chores => "Tarefas",
        HouseholdModule.Calendar => "Calendário",
        _ => "Recompensas",
    };
}
