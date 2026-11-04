using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Kubico.Api.Common;
using Kubico.Domain.Households;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Shopping;

/// <summary>
/// Um produto lido pelo código de barras, pronto a pôr na lista. <c>FromHousehold</c>: é o nome que esta casa lhe deu
/// da última vez (e não o da base pública).
/// </summary>
public record ProductDto(string Barcode, string Name, string? Category, string? Brand, string? Size, bool FromHousehold);

/// <summary>O que a base pública sabe de um produto.</summary>
public record ExternalProduct(string Name, string? Brand, string? Size, IReadOnlyList<string> Categories);

/// <summary>Base pública de produtos. Devolve null quando não conhece o código; lança se não conseguir perguntar.</summary>
public interface IProductCatalog
{
    Task<ExternalProduct?> FindAsync(string barcode, CancellationToken ct);
}

/// <summary>Open Food Facts: base aberta e gratuita de produtos alimentares, com muitos produtos portugueses.</summary>
public class OpenFoodFactsCatalog(HttpClient http) : IProductCatalog
{
    public const string UserAgent = "Kubico/1.0 (+https://github.com/Bad1ll0n/Kubico)";

    public async Task<ExternalProduct?> FindAsync(string barcode, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"https://world.openfoodfacts.org/api/v2/product/{barcode}" +
            "?fields=product_name,product_name_pt,brands,quantity,categories_tags&lc=pt",
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || status.GetInt32() != 1
            || !root.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? Text(string name) =>
            product.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

        var name = Text("product_name_pt") ?? Text("product_name");
        if (name is null)
        {
            return null;
        }

        IReadOnlyList<string> categories = product.TryGetProperty("categories_tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? [.. tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)]
            : [];
        return new ExternalProduct(name, Text("brands"), Text("quantity"), categories);
    }
}

/// <summary>
/// Ler um código de barras: primeiro o nome que a casa já lhe deu, depois a cópia guardada da base pública, e só
/// depois a base pública (Open Food Facts). Quando a pessoa põe o artigo na lista, a casa fica com esse nome.
/// </summary>
public static class ProductEndpoints
{
    public static RouteGroupBuilder MapProductEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/households/{householdId:guid}/products/{barcode}", GetProduct)
            .WithTags("Shopping")
            .RequireAuthorization();
        return api;
    }

    private static async Task<IResult> GetProduct(
        Guid householdId,
        string barcode,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        IProductCatalog catalog,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (!Barcodes.IsValid(barcode))
        {
            return Results.Problem(title: "Este código de barras não é válido.", statusCode: StatusCodes.Status400BadRequest);
        }

        var known = await db.HouseholdProducts.AsNoTracking()
            .SingleOrDefaultAsync(p => p.HouseholdId == householdId && p.Barcode == barcode, ct);
        var info = await db.ProductInfos.SingleOrDefaultAsync(p => p.Barcode == barcode, ct);
        if (known is not null)
        {
            return Results.Ok(new ProductDto(barcode, known.Name, known.Category, info?.Brand, info?.Size, true));
        }

        var now = clock.GetUtcNow();
        if (info is null || (!info.Found && now - info.FetchedAt >= ProductInfo.RetryNotFoundAfter))
        {
            ExternalProduct? found;
            try
            {
                found = await catalog.FindAsync(barcode, ct);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException
                                          || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                loggers.CreateLogger(typeof(ProductEndpoints)).LogWarning(e, "Open Food Facts não respondeu ({Barcode}).", barcode);
                return Results.Problem(
                    title: "Não foi possível procurar o produto agora. Escreve o nome à mão.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (info is null)
            {
                info = new ProductInfo { Barcode = barcode };
                db.ProductInfos.Add(info);
            }

            info.Found = found is not null;
            info.Brand = Limit(found?.Brand, 100);
            info.Size = Limit(ProductInfo.TidySize(found?.Size), 50);
            info.Name = Limit(found?.Name, 200);
            info.Category = found is null ? null : ProductCategories.FromTags(found.Categories);
            info.FetchedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return info.SuggestedName() is { } name
            ? Results.Ok(new ProductDto(barcode, name, info.Category, info.Brand, info.Size, false))
            : Results.Problem(
                title: "Não conhecemos este produto. Escreve o nome: fica guardado para a próxima vez.",
                statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>A casa passa a chamar assim a este produto (ao pô-lo na lista ou ao mudar-lhe o nome).</summary>
    internal static async Task RememberAsync(
        KubicoDbContext db, Guid householdId, string barcode, string name, string? category, DateTimeOffset now, CancellationToken ct)
    {
        var known = await db.HouseholdProducts.SingleOrDefaultAsync(p => p.HouseholdId == householdId && p.Barcode == barcode, ct);
        if (known is null)
        {
            db.HouseholdProducts.Add(new HouseholdProduct
            {
                HouseholdId = householdId, Barcode = barcode, Name = name, Category = category, UpdatedAt = now,
            });
            return;
        }

        known.Name = name;
        known.Category = category;
        known.UpdatedAt = now;
    }

    private static string? Limit(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length].TrimEnd();
}
