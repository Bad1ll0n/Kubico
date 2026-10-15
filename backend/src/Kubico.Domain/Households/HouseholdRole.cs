namespace Kubico.Domain.Households;

public enum HouseholdRole
{
    Admin = 0,
    Adult = 1,
    Child = 2,
    Guest = 3,
}

public static class HouseholdRoleExtensions
{
    /// <summary>Admin e Adulto gerem conteúdo partilhado (criar e apagar listas, etc.).</summary>
    public static bool CanManageContent(this HouseholdRole role) =>
        role is HouseholdRole.Admin or HouseholdRole.Adult;

    public static bool CanManageMembers(this HouseholdRole role) =>
        role is HouseholdRole.Admin;
}
