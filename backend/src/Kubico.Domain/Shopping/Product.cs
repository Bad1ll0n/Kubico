using System.Text.RegularExpressions;

namespace Kubico.Domain.Shopping;

/// <summary>
/// Um produto procurado pelo código de barras numa base pública (Open Food Facts). Fica guardado para não perguntar
/// outra vez; quando lá não existe também se guarda (<c>Found</c> falso) e volta-se a perguntar passado uns dias.
/// </summary>
public class ProductInfo
{
    public required string Barcode { get; set; }
    public string? Name { get; set; }
    public string? Brand { get; set; }

    /// <summary>Tamanho da embalagem, como vem na base ("1 L", "500 g").</summary>
    public string? Size { get; set; }

    public string? Category { get; set; }
    public bool Found { get; set; }
    public DateTimeOffset FetchedAt { get; set; }

    private static readonly HashSet<string> CompanyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SA", "SL", "LDA", "GMBH", "INC", "LTD", "SAS", "SPA", "SRL", "NV", "BV", "AG", "LLC", "PLC", "SERVICES",
        "COMPANY", "CORP", "CORPORATION",
    };

    /// <summary>Quanto tempo se espera antes de voltar a procurar um código que não se encontrou.</summary>
    public static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// O tamanho sem a marca de quantidade estimada ("400 g ℮", que às vezes vem escrito "400 g e").
    /// </summary>
    public static string? TidySize(string? size)
    {
        var tidy = size is null ? null : Regex.Replace(size, @"(\s+(e|℮)|℮)\s*$", "").Trim();
        return string.IsNullOrEmpty(tidy) ? null : tidy;
    }

    /// <summary>O nome a propor para a lista: o produto, a marca (se o nome não a tiver) e o tamanho.</summary>
    public string? SuggestedName()
    {
        if (Name is null)
        {
            return null;
        }

        // A marca só entra se acrescentar alguma coisa: não se o nome já a tiver ("Coca-Cola Original" e a marca
        // "COCA-COLA SERVICES SA/NV"), nem se for o nome de uma empresa ("KELLOGG ESPAÑA S.L.").
        var brand = Brand?.Split(',')[0].Trim();
        var brandWords = brand?.Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        var parts = new List<string> { Name };
        if (brandWords.Length > 0 && brand!.Length <= 25
            && !Name.Contains(brandWords[0], StringComparison.OrdinalIgnoreCase)
            && !brandWords.Any(w => CompanyWords.Contains(w.Replace(".", ""))))
        {
            parts.Add(brand);
        }

        var size = TidySize(Size);
        if (size is not null && !Name.Contains(size, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(size);
        }

        var name = string.Join(' ', parts);
        return name.Length <= 200 ? name : name[..200].TrimEnd();
    }
}

/// <summary>Como esta casa chama a um produto: o nome e a categoria com que o puseram na lista da última vez.</summary>
public class HouseholdProduct
{
    public Guid HouseholdId { get; set; }
    public required string Barcode { get; set; }
    public required string Name { get; set; }
    public string? Category { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class Barcodes
{
    /// <summary>
    /// EAN-8, UPC-A, EAN-13 e ITF-14: só algarismos e com o dígito de controlo certo (assim um código mal lido pela
    /// câmara não vai procurar o produto errado).
    /// </summary>
    public static bool IsValid(string? code)
    {
        if (code is null || code.Length is not (8 or 12 or 13 or 14) || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = code.Length - 2; i >= 0; i--)
        {
            // O algarismo mesmo antes do de controlo pesa 3, o seguinte 1, e assim alternadamente.
            var weight = (code.Length - 2 - i) % 2 == 0 ? 3 : 1;
            sum += (code[i] - '0') * weight;
        }

        return (10 - sum % 10) % 10 == code[^1] - '0';
    }
}

/// <summary>As secções do supermercado em que a app arruma os artigos, a partir das categorias do Open Food Facts.</summary>
public static class ProductCategories
{
    // Por ordem: a primeira que bater ganha (congelados antes de carne, por exemplo).
    private static readonly (string Category, string[] Tags)[] Map =
    [
        ("Congelados", ["en:frozen-foods", "en:frozen-desserts", "en:ice-creams"]),
        ("Bebidas", ["en:beverages", "en:waters", "en:juices", "en:sodas", "en:wines", "en:beers", "en:coffees", "en:teas"]),
        ("Laticínios", ["en:dairies", "en:milks", "en:cheeses", "en:yogurts", "en:butters", "en:creams"]),
        ("Frutas e legumes", ["en:fruits", "en:vegetables", "en:fresh-vegetables", "en:fresh-fruits", "en:legumes"]),
        ("Padaria", ["en:breads", "en:pastries", "en:viennoiseries"]),
        ("Carne e peixe", ["en:meats", "en:fishes", "en:seafood", "en:poultries", "en:hams", "en:sausages"]),
        ("Doces e snacks", ["en:sweet-snacks", "en:chocolates", "en:biscuits", "en:candies", "en:salty-snacks", "en:snacks"]),
        ("Mercearia", ["en:pastas", "en:rices", "en:breakfast-cereals", "en:canned-foods", "en:condiments", "en:sauces",
            "en:vegetable-oils", "en:spices", "en:flours", "en:sugars", "en:spreads", "en:breakfasts", "en:honeys",
            "en:jams", "en:groceries"]),
        ("Bebé", ["en:baby-foods"]),
    ];

    public static string? FromTags(IEnumerable<string> tags)
    {
        var set = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Map.FirstOrDefault(m => m.Tags.Any(set.Contains)).Category;
    }
}
