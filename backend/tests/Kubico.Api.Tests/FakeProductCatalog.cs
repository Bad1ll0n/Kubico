using System.Collections.Concurrent;
using Kubico.Api.Shopping;

namespace Kubico.Api.Tests;

/// <summary>Base pública de produtos dos testes: conhece os produtos que lhe ensinam e conta as perguntas.</summary>
public sealed class FakeProductCatalog : IProductCatalog
{
    private readonly ConcurrentDictionary<string, ExternalProduct> _products = new();
    private readonly ConcurrentDictionary<string, byte> _unreachable = new();
    private readonly ConcurrentQueue<string> _lookups = new();

    public void Add(string barcode, ExternalProduct product) => _products[barcode] = product;

    /// <summary>Perguntar por este código falha como se não houvesse rede (até <see cref="Reachable"/>).</summary>
    public void Unreachable(string barcode) => _unreachable[barcode] = 0;

    public void Reachable(string barcode) => _unreachable.TryRemove(barcode, out _);

    public int Lookups(string barcode) => _lookups.Count(b => b == barcode);

    public Task<ExternalProduct?> FindAsync(string barcode, CancellationToken ct)
    {
        _lookups.Enqueue(barcode);
        if (_unreachable.ContainsKey(barcode))
        {
            throw new HttpRequestException("Sem ligação ao Open Food Facts.");
        }

        return Task.FromResult(_products.GetValueOrDefault(barcode));
    }

    /// <summary>Um EAN-13 válido a partir de 12 algarismos (acrescenta o dígito de controlo).</summary>
    public static string Ean13(string first12)
    {
        var sum = first12.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return first12 + (10 - sum % 10) % 10;
    }
}
