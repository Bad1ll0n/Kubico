namespace Kubico.Api.Common;

/// <summary>
/// As datas que a API aceita nos pedidos (1900 a 2200). Fora disto, as contas das repetições (o dia anterior, o
/// seguinte) saem do intervalo do DateOnly e a área inteira deixava de abrir.
/// </summary>
public static class SupportedDates
{
    public const string OutOfRangeMessage = "As datas têm de estar entre 1900 e 2200.";

    public static bool IsSupported(DateOnly date) => date.Year is >= 1900 and <= 2200;

    public static bool IsSupported(DateOnly? date) => date is not { } value || IsSupported(value);
}
