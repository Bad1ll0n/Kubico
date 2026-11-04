namespace Kubico.Domain;

/// <summary>
/// Entidade com concorrência otimista. A <see cref="Version"/> muda em cada gravação (o <c>KubicoDbContext</c> trata
/// disso) e o UPDATE só passa se ninguém a mudou entretanto: dois pedidos em simultâneo sobre a mesma coisa não se
/// pisam, o segundo recebe <c>DbUpdateConcurrencyException</c> (a API responde 409).
/// </summary>
public interface IVersioned
{
    Guid Version { get; set; }
}
