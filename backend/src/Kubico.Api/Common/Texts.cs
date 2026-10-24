using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Common;

/// <summary>
/// Os textos que o servidor mostra às pessoas. No código escrevem-se em português; aqui fica a tradução para
/// inglês. Nos textos com partes variáveis, {0}, {1}… ficam como estão (nomes, títulos, números) e {*0} também se
/// traduz (por exemplo, o resumo de uma alteração dentro de "desfez: …").
/// </summary>
public static partial class Texts
{
    public const string Portuguese = "pt";
    public const string English = "en";

    public static readonly IReadOnlyList<string> Languages = [Portuguese, English];

    /// <summary>"en", "en-GB"… é inglês; tudo o resto é português.</summary>
    public static string Normalize(string? language) =>
        language?.Trim().StartsWith(English, StringComparison.OrdinalIgnoreCase) == true ? English : Portuguese;

    /// <summary>A língua pedida no cabeçalho Accept-Language (a app envia a que a pessoa escolheu).</summary>
    public static string LanguageOf(HttpContext? context) =>
        context?.Request.Headers.AcceptLanguage.ToString() is { Length: > 0 } header
            ? Normalize(header.Split(',')[0].Split(';')[0])
            : Portuguese;

    /// <summary>O texto na língua pedida (o português fica igual; um texto sem tradução também).</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Translate(string? text, string language)
    {
        if (language != English || string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (Exact.TryGetValue(text, out var english))
        {
            return english;
        }

        foreach (var pattern in Patterns)
        {
            var match = pattern.Regex.Match(text);
            if (match.Success)
            {
                return pattern.Format(match, language);
            }
        }

        return text;
    }

    /// <summary>Um texto com partes variáveis composto já na língua de quem o vai ler.</summary>
    public static string Format(string language, string portuguese, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Template(portuguese, language), args);

    /// <summary>
    /// Os erros na língua de quem pede: o título e as mensagens de validação. As mensagens das anotações
    /// ([Required], [StringLength]…) vêm do .NET em inglês e passam a frases simples nas duas línguas.
    /// </summary>
    public static void Localize(ProblemDetails problem, string language)
    {
        if (problem is HttpValidationProblemDetails validation)
        {
            foreach (var (key, messages) in validation.Errors.ToList())
            {
                validation.Errors[key] = [.. messages.Select(m => Validation(m, language) ?? Translate(m, language))];
            }
        }

        problem.Title = problem.Title switch
        {
            null => null,
            "One or more validation errors occurred." or "Bad Request" => language == English
                ? "Please check the details you entered."
                : "Verifica os dados introduzidos.",
            var title => Translate(title, language),
        };
    }

    // --- Mensagens das anotações de validação ---------------------------------

    /// <summary>Como se chama cada campo dos pedidos (artigo e nome em português; nome em inglês).</summary>
    private static readonly Dictionary<string, (string Pt, string En)> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Email"] = ("o email", "email"),
        ["Password"] = ("a palavra-passe", "password"),
        ["DisplayName"] = ("o nome", "name"),
        ["Name"] = ("o nome", "name"),
        ["Title"] = ("o título", "title"),
        ["Notes"] = ("as notas", "notes"),
        ["Location"] = ("o local", "location"),
        ["Recurrence"] = ("a repetição", "repetition"),
        ["Pin"] = ("o PIN", "PIN"),
        ["Code"] = ("o código", "code"),
        ["FamilyCode"] = ("o código da família", "family code"),
        ["Reason"] = ("o motivo", "reason"),
        ["Description"] = ("a descrição", "description"),
        ["Points"] = ("os pontos", "points"),
        ["Cost"] = ("o custo", "cost"),
        ["AmountCents"] = ("o valor", "amount"),
        ["BonusPerChoreCents"] = ("o extra por tarefa", "bonus per chore"),
        ["TimeZoneId"] = ("o fuso horário", "time zone"),
        ["Platform"] = ("a plataforma", "platform"),
        ["Token"] = ("o identificador do dispositivo", "device identifier"),
        ["RefreshToken"] = ("a sessão", "session"),
        ["ChallengeToken"] = ("o pedido", "request"),
        ["Roles"] = ("as permissões", "permissions"),
    };

    private static string? Validation(string message, string language)
    {
        var match = AnnotationMessage().Match(message);
        if (!match.Success)
        {
            return null;
        }

        var (pt, en) = Fields.TryGetValue(match.Groups["field"].Value, out var names)
            ? names
            : ($"o campo «{match.Groups["field"].Value}»", $"field “{match.Groups["field"].Value}”");
        var (min, max) = (match.Groups["min"].Value, match.Groups["max"].Value);
        var plural = pt.StartsWith("os ", StringComparison.Ordinal) || pt.StartsWith("as ", StringComparison.Ordinal);
        var feminine = pt.StartsWith("a", StringComparison.Ordinal);
        var valid = "válid" + (feminine ? "a" : "o") + (plural ? "s" : "");
        var has = plural ? "têm" : "tem";
        var subject = char.ToUpperInvariant(pt[0]) + pt[1..];

        var kind = new[] { "required", "between", "maximum", "range" }.FirstOrDefault(k => match.Groups[k].Success) ?? "invalid";
        return (kind, language) switch
        {
            ("required", English) => $"The {en} is required.",
            ("required", _) => $"{(plural ? "Faltam" : "Falta")} {pt}.",
            ("between", English) => $"The {en} must have between {min} and {max} characters.",
            ("between", _) => $"{subject} {has} de ter entre {min} e {max} caracteres.",
            ("maximum", English) => $"The {en} can have at most {max} characters.",
            ("maximum", _) => $"{subject} {has} de ter no máximo {max} caracteres.",
            ("range", English) => $"The {en} must be between {min} and {max}.",
            ("range", _) => $"{subject} {has} de estar entre {min} e {max}.",
            (_, English) => $"The {en} isn't valid.",
            _ => $"{subject} não {(plural ? "são" : "é")} {valid}.",
        };
    }

    [GeneratedRegex(
        @"^(?:The (?<field>\w+) field is required\.(?<required>)|" +
        @"The (?<field>\w+) field is not a valid e-mail address\.|" +
        @"The field (?<field>\w+) must be a string with a minimum length of (?<min>\d+) and a maximum length of (?<max>\d+)\.(?<between>)|" +
        @"The field (?<field>\w+) must be a string with a maximum length of (?<max>\d+)\.(?<maximum>)|" +
        @"The field (?<field>\w+) must be between (?<min>\S+) and (?<max>\S+)\.(?<range>)|" +
        @"The field (?<field>\w+) must match the regular expression .*)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AnnotationMessage();

    // --- Catálogo ---------------------------------------------------------------

    /// <summary>A tradução de um texto-modelo ("Kubico: {0} novidades"), para depois se preencher.</summary>
    private static string Template(string portuguese, string language) =>
        language == English && Catalog.TryGetValue(portuguese, out var english) ? english : portuguese;

    private static readonly Dictionary<string, string> Catalog = new()
    {
        // Pedidos inválidos, concorrência e limites
        ["Pedido inválido."] = "Invalid request.",
        ["Alguém mudou isto ao mesmo tempo. Atualiza e tenta outra vez."] =
            "Someone changed this at the same time. Refresh and try again.",
        ["Demasiados pedidos. Tenta outra vez daqui a {0} segundos."] = "Too many requests. Try again in {0} seconds.",
        ["Demasiadas tentativas erradas. Tenta outra vez daqui a {0} minutos."] = "Too many wrong attempts. Try again in {0} minutes.",
        ["Demasiadas tentativas erradas. Tenta outra vez daqui a um minuto."] = "Too many wrong attempts. Try again in a minute.",
        ["Demasiados códigos errados. Tenta outra vez daqui a {0} minutos."] = "Too many wrong codes. Try again in {0} minutes.",

        // Histórico de alterações
        ["Esta alteração não se pode desfazer."] = "This change can't be undone.",
        ["Esta alteração já foi desfeita."] = "This change has already been undone.",
        ["Só quem fez esta alteração (ou quem gere esta área) a pode desfazer."] =
            "Only the person who made this change (or who manages this area) can undo it.",
        ["Entretanto houve outras alterações a isto. Desfaz primeiro as mais recentes."] =
            "There have been other changes to this since. Undo the most recent ones first.",
        ["desfez: {*0}"] = "undid: {0}",
        ["Já não tens acesso para desfazer esta alteração."] = "You no longer have access to undo this change.",

        // Conta, sessão e verificação em dois passos
        ["Os perfis de criança entram com o PIN e não têm verificação em dois passos."] =
            "Child profiles sign in with a PIN and don't have two-step verification.",
        ["A verificação em dois passos já está ligada."] = "Two-step verification is already on.",
        ["O código não está certo. Confirma que o telemóvel está com a hora certa e tenta outra vez."] =
            "The code isn't right. Check that your phone's clock is correct and try again.",
        ["A palavra-passe não está certa."] = "The password isn't right.",
        ["O código não está certo."] = "The code isn't right.",
        ["Um perfil de criança é apagado por um adulto da casa."] = "A child profile is deleted by an adult in the household.",
        ["Este email já está registado."] = "This email is already registered.",
        ["Email ou palavra-passe incorretos."] = "Incorrect email or password.",
        ["Passou demasiado tempo. Entra outra vez."] = "Too much time has passed. Sign in again.",
        ["Demasiados códigos errados. Tenta outra vez daqui a 15 minutos."] = "Too many wrong codes. Try again in 15 minutes.",
        ["A sessão terminou. Entra novamente."] = "Your session has ended. Sign in again.",
        ["PIN errado."] = "Wrong PIN.",
        ["Código da família inválido. Pede-o a um adulto da casa."] =
            "Invalid family code. Ask an adult in the household for it.",
        ["Demasiados PINs errados. Tenta outra vez daqui a {0} minutos."] = "Too many wrong PINs. Try again in {0} minutes.",
        ["Escolhe português (pt) ou inglês (en)."] = "Choose Portuguese (pt) or English (en).",
        ["Antes de apagares a conta, passa a administração de «{0}» a um adulto."] =
            "Before you delete your account, hand over the administration of “{0}” to an adult.",

        // Calendário
        ["Indica um intervalo de datas válido, com {0} dias no máximo."] = "Choose a valid date range, of at most {0} days.",
        ["criou o evento «{0}»"] = "created the event “{0}”",
        ["mudou o evento «{0}»"] = "changed the event “{0}”",
        ["apagou o evento «{0}»"] = "deleted the event “{0}”",
        ["mudou «{0}» a partir de {1}"] = "changed “{0}” from {1} onwards",
        ["mudou «{0}» no dia {1}"] = "changed “{0}” on {1}",
        ["apagou «{0}» a partir de {1}"] = "deleted “{0}” from {1} onwards",
        ["apagou «{0}» no dia {1}"] = "deleted “{0}” on {1}",
        ["Uma alteração a um só dia de uma série não se pode repetir."] = "A change to a single day of a series can't repeat.",
        ["{0} (todo o dia)"] = "{0} (all day)",
        ["{0} às {1}"] = "{0} at {1}",
        ["Tens de levar e ir buscar: {0}"] = "You're dropping off and picking up: {0}",
        ["Tens de levar: {0}"] = "You're dropping off: {0}",
        ["Tens de ir buscar: {0}"] = "You're picking up: {0}",
        ["Novo evento: {0}"] = "New event: {0}",
        ["Indica se é só este evento (this) ou este e os seguintes (following)."] =
            "Say whether it's just this event (this) or this and the following ones (following).",
        ["Indica o dia do evento."] = "Choose the day of the event.",
        ["O último dia não pode ser antes do primeiro."] = "The last day can't be before the first.",
        ["Indica quando o evento começa e acaba."] = "Choose when the event starts and ends.",
        ["O fim tem de ser depois do início."] = "The end must be after the start.",
        ["Esta repetição não é válida ou ainda não é suportada."] = "This repetition isn't valid or isn't supported yet.",
        ["Os participantes e as boleias têm de ser membros desta casa."] =
            "Participants and rides must be members of this household.",
        ["As datas têm de estar entre 1900 e 2200."] = "Dates must be between 1900 and 2200.",
        ["Participantes: {0}"] = "Participants: {0}",
        ["Uma criança não pode levar nem ir buscar: escolhe um adulto."] = "A child can't drop off or pick up: choose an adult.",
        ["O texto tem caracteres que não se podem usar."] = "The text has characters that can't be used.",
        ["Leva: {0}"] = "Drops off: {0}",
        ["Vai buscar: {0}"] = "Picks up: {0}",

        // Feriados
        ["Ano Novo"] = "New Year's Day",
        ["Sexta-feira Santa"] = "Good Friday",
        ["Páscoa"] = "Easter",
        ["Dia da Liberdade"] = "Freedom Day",
        ["Dia do Trabalhador"] = "Labour Day",
        ["Corpo de Deus"] = "Corpus Christi",
        ["Dia de Portugal"] = "Portugal Day",
        ["Assunção de Nossa Senhora"] = "Assumption Day",
        ["Implantação da República"] = "Republic Day",
        ["Todos os Santos"] = "All Saints' Day",
        ["Restauração da Independência"] = "Restoration of Independence",
        ["Imaculada Conceição"] = "Immaculate Conception",
        ["Natal"] = "Christmas Day",

        // Google Calendar e Outlook
        ["O calendário do Kubico já não existe na conta ligada."] = "The Kubico calendar no longer exists in the connected account.",
        ["A ligação ao {0} ainda não está configurada neste servidor."] = "The connection to {0} isn't set up on this server yet.",
        ["Ligação cancelada"] = "Connection cancelled",
        ["Não foi dada autorização ao Kubico. Podes fechar esta página e tentar outra vez na app."] =
            "Kubico wasn't given permission. You can close this page and try again in the app.",
        ["Pedido inválido"] = "Invalid request",
        ["Este endereço não é válido. Volta à app do Kubico e tenta outra vez."] =
            "This address isn't valid. Go back to the Kubico app and try again.",
        ["O pedido expirou"] = "The request has expired",
        ["Passou demasiado tempo. Volta à app do Kubico e tenta outra vez."] =
            "Too much time has passed. Go back to the Kubico app and try again.",
        ["Sem permissão"] = "No permission",
        ["Já não podes ligar calendários nesta casa."] = "You can no longer connect calendars in this household.",
        ["Indisponível"] = "Unavailable",
        ["A ligação ao {0} não está configurada neste servidor."] = "The connection to {0} isn't set up on this server.",
        ["Não foi possível ligar"] = "Couldn't connect",
        ["O {0} não deu acesso permanente. Tenta outra vez."] = "{0} didn't grant lasting access. Try again.",
        ["O {0} não confirmou a autorização. Tenta outra vez."] = "{0} didn't confirm the authorisation. Try again.",
        ["Calendário ligado"] = "Calendar connected",
        ["Os eventos da casa vão aparecer no calendário «{0}» da conta {1}. Já podes voltar ao Kubico."] =
            "The household's events will appear in the “{0}” calendar of the {1} account. You can go back to Kubico now.",
        ["Este serviço deixou de estar disponível no servidor do Kubico."] = "This service is no longer available on the Kubico server.",
        ["Já não tens acesso ao calendário desta casa."] = "You no longer have access to this household's calendar.",
        ["O calendário do Kubico tinha sido apagado na conta e foi criado de novo."] =
            "The Kubico calendar had been deleted from the account and was created again.",
        ["O acesso à conta expirou ou foi retirado. Volta a ligar a conta."] =
            "Access to the account expired or was removed. Connect the account again.",
        ["Não foi possível sincronizar agora; o Kubico volta a tentar daqui a pouco."] =
            "Couldn't sync right now; Kubico will try again shortly.",
        ["«{0}» repete-se de uma forma que o Kubico ainda não suporta e não foi sincronizado."] =
            "“{0}” repeats in a way Kubico doesn't support yet and wasn't synced.",
        ["«{0}» tem datas fora do que o Kubico aceita (1900 a 2200) e não foi sincronizado."] =
            "“{0}” has dates outside what Kubico accepts (1900 to 2200) and wasn't synced.",
        ["«{0}»: o dia alterado não foi encontrado na cópia da série."] = "“{0}”: the changed day wasn't found in the copy of the series.",
        ["«{0}» não foi copiado; o Kubico volta a tentar."] = "“{0}” wasn't copied; Kubico will try again.",
        ["O Google não devolveu o calendário criado."] = "Google didn't return the created calendar.",
        ["O Google não devolveu o evento."] = "Google didn't return the event.",
        ["O Outlook não devolveu o calendário criado."] = "Outlook didn't return the created calendar.",
        ["O Outlook não devolveu o evento."] = "Outlook didn't return the event.",
        ["O {0} recusou o token de acesso."] = "{0} refused the access token.",
        ["O {0} devolveu uma resposta inválida ({1})."] = "{0} returned an invalid response ({1}).",
        ["O {0} recusou a autorização ({1})."] = "{0} refused the authorisation ({1}).",
        ["O {0} recusou o pedido de token ({1} {2})."] = "{0} refused the token request ({1} {2}).",
        ["O {0} não devolveu um token de acesso."] = "{0} didn't return an access token.",
        ["A ligação ao iCloud ainda não está configurada neste servidor."] = "The iCloud connection isn't set up on this server yet.",
        ["O Apple ID ou a palavra-passe da app não estão certos. Confirma que usas uma palavra-passe específica da app."] =
            "The Apple ID or the app password isn't right. Make sure you're using an app-specific password.",
        ["Não foi possível ligar ao iCloud agora. Tenta outra vez daqui a pouco."] =
            "Couldn't connect to iCloud right now. Try again shortly.",
        ["Confirma a ligação"] = "Confirm the connection",
        ["{0} quer mostrar os eventos da casa «{1}» do Kubico no calendário «{2}» da conta {3}, e o que mudar lá muda também no Kubico. Só confirma se foste tu a pedir esta ligação."] =
            "{0} wants to show the events of the Kubico household “{1}” in the “{2}” calendar of the {3} account, and changes made there will also change Kubico. Only confirm if you asked for this connection.",
        ["Ligar"] = "Connect",
        ["Cancelar"] = "Cancel",

        // Tarefas
        ["criou a tarefa «{0}»"] = "created the chore “{0}”",
        ["mudou a tarefa «{0}»"] = "changed the chore “{0}”",
        ["apagou a tarefa «{0}»"] = "deleted the chore “{0}”",
        ["fez «{0}»"] = "did “{0}”",
        ["marcou «{0}» como feita por {1}"] = "marked “{0}” as done by {1}",
        ["Esta tarefa já está feita. Volta a estar disponível a {0}."] = "This chore is already done. It'll be available again on {0}.",
        ["Quem fez a tarefa tem de ser membro desta casa."] = "The person who did the chore must be a member of this household.",
        ["Só quem gere as tarefas pode marcar uma tarefa como feita por outra pessoa."] =
            "Only people who manage chores can mark a chore as done by someone else.",
        ["Essa repetição não existe."] = "That repetition doesn't exist.",
        ["Essa altura do dia não existe."] = "That time of day doesn't exist.",
        ["Os pontos desta tarefa já foram trocados: {0} ficaria a dever {1} pontos."] =
            "The points for this chore have already been spent: {0} would owe {1} points.",
        ["não aceitou «{0}», feita por {1}"] = "didn't accept “{0}”, done by {1}",
        ["não aceitou «{0}», feita por antigo membro"] = "didn't accept “{0}”, done by a former member",
        ["Tarefa para ti: {0} ({1} está fora)"] = "A chore for you: {0} ({1} is away)",
        ["Tarefa para ti: {0}"] = "A chore for you: {0}",
        ["É a tua vez: {0}"] = "It's your turn: {0}",
        ["{0} fez: {1}"] = "{0} did: {1}",
        ["Com foto. Vê no histórico das tarefas."] = "With a photo. See it in the chore history.",
        ["Prazo: {0}"] = "Due: {0}",
        ["Faz outra vez: {0}"] = "Do it again: {0}",
        ["Não foi aceite, os {0} pontos não contam."] = "It wasn't accepted; the {0} points don't count.",
        ["Não foi aceite."] = "It wasn't accepted.",
        ["Esta tarefa precisa de uma foto do trabalho feito."] = "This chore needs a photo of the finished work.",
        ["Esta foto não serve: envia outra."] = "This photo can't be used: send another one.",
        ["Esta tarefa já está concluída."] = "This chore is already done.",
        ["Esta tarefa está atribuída a outra pessoa."] = "This chore is assigned to someone else.",
        ["O responsável e a rotação têm de ser membros desta casa."] =
            "The person responsible and the rotation must be members of this household.",
        ["Cada membro só pode aparecer uma vez na rotação."] = "Each member can only appear once in the rotation.",
        ["A rotação só faz sentido em tarefas que se repetem."] = "Rotation only makes sense for repeating chores.",
        ["Uma rotina tem no máximo {0} passos."] = "A routine has at most {0} steps.",
        ["Cada passo tem no máximo 100 letras."] = "Each step can have at most 100 characters.",
        ["A foto tem de ter no máximo 5 MB."] = "The photo can be at most 5 MB.",
        ["Isto não parece uma foto (JPEG, PNG ou WebP)."] = "This doesn't look like a photo (JPEG, PNG or WebP).",
        ["Esta tarefa já não foi aceite."] = "This chore has already been rejected.",

        // Permissões
        ["Não tens permissão para esta ação neste agregado."] = "You don't have permission to do this in this household.",
        ["Não tens permissão para isto em Compras."] = "You don't have permission for this in Shopping.",
        ["Não tens permissão para isto em Tarefas."] = "You don't have permission for this in Chores.",
        ["Não tens permissão para isto em Calendário."] = "You don't have permission for this in Calendar.",
        ["Não tens permissão para isto em Recompensas."] = "You don't have permission for this in Rewards.",
        ["O administrador tem sempre acesso a tudo."] = "The admin always has access to everything.",
        ["Esse acesso não existe em Compras."] = "That access level doesn't exist in Shopping.",
        ["Esse acesso não existe em Tarefas."] = "That access level doesn't exist in Chores.",
        ["Esse acesso não existe em Calendário."] = "That access level doesn't exist in Calendar.",
        ["Esse acesso não existe em Recompensas."] = "That access level doesn't exist in Rewards.",
        ["Essa área não existe."] = "That area doesn't exist.",
        ["Não pertences a este agregado."] = "You're not a member of this household.",

        // Casas, membros e ausências
        ["Só podes marcar as tuas ausências."] = "You can only set your own absences.",
        ["Só quem gere as tarefas marca ou cancela as ausências dos outros."] =
            "Only people who manage chores can set or cancel other people's absences.",
        ["As ausências das crianças são marcadas por um adulto da casa."] = "Children's absences are set by an adult in the household.",
        ["Já está marcada uma ausência nesses dias."] = "An absence is already set for those days.",
        ["vai estar fora de {0} a {1}"] = "will be away from {0} to {1}",
        ["marcou a ausência de {0} de {1} a {2}"] = "set {0}'s absence from {1} to {2}",
        ["cancelou a sua ausência"] = "cancelled their absence",
        ["cancelou a ausência de {0}"] = "cancelled {0}'s absence",
        ["mudou as permissões dos Adultos"] = "changed the Adults' permissions",
        ["mudou as permissões das Crianças"] = "changed the Children's permissions",
        ["mudou as permissões dos Convidados"] = "changed the Guests' permissions",
        ["mudou o PIN de {0}"] = "changed {0}'s PIN",
        ["terminou as sessões de {0}"] = "signed {0} out on every device",
        ["renovou o código da família"] = "renewed the family code",
        ["anulou o convite {0}"] = "cancelled the invite {0}",
        ["Esse papel não existe."] = "That role doesn't exist.",
        ["Não se convida para administrador: depois de a pessoa entrar, usa «Passar a administração»."] =
            "You can't invite someone as admin: once they've joined, use “Hand over administration”.",
        ["O convite pode servir para 1 a 50 pessoas."] = "The invite can be used by 1 to 50 people.",
        ["A data de nascimento não pode ser no futuro, e um perfil de criança é para quem tem menos de 18 anos."] =
            "The date of birth can't be in the future, and a child profile is for people under 18.",
        ["Escolhe um membro desta casa."] = "Choose a member of this household.",
        ["Indica até quando (no máximo um ano, e não antes do primeiro dia)."] =
            "Choose until when (at most a year, and not before the first day).",
        ["Essa ausência já acabou."] = "That absence has already ended.",
        ["Só podes cancelar as tuas ausências."] = "You can only cancel your own absences.",
        ["criou o perfil de criança de {0}"] = "created a child profile for {0}",
        ["O PIN tem de ter 4 a 6 algarismos."] = "The PIN must have 4 to 6 digits.",
        ["Fuso horário desconhecido (usa um nome IANA, ex.: Europe/Lisbon)."] =
            "Unknown time zone (use an IANA name, e.g. Europe/London).",
        ["Esse modelo de casa não existe."] = "That household template doesn't exist.",
        ["O convite tem de ser válido entre 1 e 30 dias."] = "The invite must be valid for 1 to 30 days.",
        ["Convite inválido ou expirado."] = "Invalid or expired invite.",
        ["entrou na casa"] = "joined the household",
        ["saiu da casa"] = "left the household",
        ["tirou {0} da casa"] = "removed {0} from the household",
        ["passou a administração a {0}"] = "handed administration to {0}",
        ["mudou o papel de {0} para Administrador"] = "changed {0}'s role to Admin",
        ["mudou o papel de {0} para Adulto"] = "changed {0}'s role to Adult",
        ["mudou o papel de {0} para Criança"] = "changed {0}'s role to Child",
        ["mudou o papel de {0} para Convidado"] = "changed {0}'s role to Guest",
        ["Os perfis de criança não podem criar nem juntar-se a outras casas."] =
            "Child profiles can't create or join other households.",
        ["Um perfil de criança é sempre Criança. Para outro papel, a pessoa precisa de conta própria."] =
            "A child profile is always a Child. For another role, the person needs their own account.",
        ["Para saíres da casa, usa a opção Sair desta casa."] = "To leave the household, use the Leave this household option.",
        ["Pede a um adulto da casa para tirar o teu perfil."] = "Ask an adult in the household to remove your profile.",
        ["Antes de saíres, passa a administração da casa a outra pessoa."] =
            "Before you leave, hand over the household administration to someone else.",
        ["A administração só pode passar para alguém com conta própria."] =
            "Administration can only be handed to someone with their own account.",
        ["A casa precisa de pelo menos um administrador. Passa a administração a outra pessoa primeiro."] =
            "The household needs at least one admin. Hand over administration to someone else first.",

        // Modelos de casa
        ["Família com crianças"] = "Family with children",
        ["Rotinas da manhã e da noite para os mais novos, tarefas da casa com pontos e recompensas para trocar."] =
            "Morning and evening routines for the little ones, household chores with points, and rewards to trade them for.",
        ["Casal"] = "Couple",
        ["As tarefas da casa a dividir por dois e a lista do supermercado."] =
            "Household chores split between two, and the supermarket list.",
        ["Casa partilhada"] = "Shared house",
        ["Para quem divide casa com amigos ou colegas: as limpezas das zonas comuns e a lista das compras comuns."] =
            "For people sharing a home with friends or flatmates: cleaning the common areas and a shared shopping list.",
        ["Cuidar dos pais ou avós"] = "Caring for parents or grandparents",
        ["A medicação, as compras e as visitas organizadas entre a família."] =
            "Medication, shopping and visits organised across the family.",
        ["Começar do zero"] = "Start from scratch",
        ["Uma casa vazia: crias tu as listas e as tarefas."] = "An empty household: you create the lists and chores.",
        ["Supermercado"] = "Supermarket",
        ["Farmácia"] = "Pharmacy",
        ["Compras da casa"] = "House shopping",
        ["Compras para os pais"] = "Shopping for the parents",
        ["Rotina da manhã"] = "Morning routine",
        ["Rotina da noite"] = "Evening routine",
        ["Lavar os dentes"] = "Brush teeth",
        ["Vestir"] = "Get dressed",
        ["Fazer a cama"] = "Make the bed",
        ["Arrumar os brinquedos"] = "Tidy up the toys",
        ["Vestir o pijama"] = "Put on pyjamas",
        ["Pôr a mesa"] = "Set the table",
        ["Tirar o lixo"] = "Take out the rubbish",
        ["Aspirar a casa"] = "Vacuum the house",
        ["Lavar a roupa"] = "Do the laundry",
        ["Limpar a casa de banho"] = "Clean the bathroom",
        ["Regar as plantas"] = "Water the plants",
        ["Tirar o lixo e a reciclagem"] = "Take out the rubbish and recycling",
        ["Limpar a cozinha"] = "Clean the kitchen",
        ["Aspirar as zonas comuns"] = "Vacuum the common areas",
        ["Preparar a caixa dos medicamentos"] = "Fill the pill organiser",
        ["Fazer as compras dos pais"] = "Do the parents' shopping",
        ["Visita"] = "Visit",
        ["Telefonar a saber como estão"] = "Call to see how they are",
        ["Escolher o filme de sexta"] = "Pick Friday's film",
        ["Meia hora extra de ecrã"] = "Half an hour of extra screen time",
        ["Um passeio especial"] = "A special outing",

        // Avisos
        ["Kubico: {0} novidades"] = "Kubico: {0} updates",
        ["{0} e mais {1}"] = "{0} and {1} more",
        ["Indica quando começam e quando acabam as horas de silêncio (horas diferentes)."] =
            "Choose when quiet hours start and end (different times).",
        ["Fuso horário desconhecido."] = "Unknown time zone.",

        // Recompensas e mesada
        ["criou a recompensa «{0}»"] = "created the reward “{0}”",
        ["mudou a recompensa «{0}»"] = "changed the reward “{0}”",
        ["apagou a recompensa «{0}»"] = "deleted the reward “{0}”",
        ["Faltam {0} pontos para esta recompensa."] = "You need {0} more points for this reward.",
        ["Os pontos de {0} já não chegam para este pedido."] = "{0} no longer has enough points for this request.",
        ["pediu «{0}»"] = "asked for “{0}”",
        ["aprovou «{0}» a {1}"] = "approved “{0}” for {1}",
        ["recusou «{0}» a {1}"] = "declined “{0}” for {1}",
        ["cancelou o pedido «{0}»"] = "cancelled the request “{0}”",
        ["cancelou o pedido «{0}» de {1}"] = "cancelled {1}'s request “{0}”",
        ["definiu a mesada de {0}"] = "set {0}'s allowance",
        ["acabou com a mesada de {0}"] = "stopped {0}'s allowance",
        ["pagou a mesada de {0}: {1},{2} €"] = "paid {0}'s allowance: €{1}.{2}",
        ["pagou o resto da mesada de {0}: {1},{2} €"] = "paid the rest of {0}'s allowance: €{1}.{2}",
        ["anulou um pagamento da mesada de {0}"] = "cancelled a payment of {0}'s allowance",
        ["Recebeste o resto da mesada: {0},{1} €"] = "You got the rest of your allowance: €{0}.{1}",
        ["{0} pediu: {1}"] = "{0} asked for: {1}",
        ["{0} pontos. Aprova ou recusa na app."] = "{0} points. Approve or decline in the app.",
        ["Aprovado: {0}"] = "Approved: {0}",
        ["Recusado: {0}"] = "Declined: {0}",
        ["Aproveita!"] = "Enjoy!",
        ["Os pontos voltaram para ti."] = "Your points have been returned.",
        ["Só quem fez o pedido, ou um adulto, o pode cancelar."] =
            "Only the person who made the request, or an adult, can cancel it.",
        ["Este pedido já foi decidido."] = "This request has already been decided.",
        ["Escolhe semanal ou mensal."] = "Choose weekly or monthly.",
        ["Esse período não é desta mesada."] = "That period isn't part of this allowance.",
        ["Esse período já está pago."] = "That period has already been paid.",
        ["Recebeste a mesada: {0},{1} €"] = "You got your allowance: €{0}.{1}",
        ["Com 1 tarefa feita."] = "With 1 chore done.",
        ["Com {0} tarefas feitas."] = "With {0} chores done.",

        // Compras
        ["Este código de barras não é válido."] = "This barcode isn't valid.",
        ["Não foi possível procurar o produto agora. Escreve o nome à mão."] =
            "Couldn't look up the product right now. Type the name in.",
        ["Não conhecemos este produto. Escreve o nome: fica guardado para a próxima vez."] =
            "We don't know this product. Type its name: it'll be saved for next time.",
        ["criou a lista «{0}»"] = "created the list “{0}”",
        ["apagou a lista «{0}»"] = "deleted the list “{0}”",
        ["Este artigo já existe noutra lista."] = "This item already exists in another list.",
        ["O nome é obrigatório."] = "The name is required.",
        ["pôs «{0}» na lista «{1}»"] = "added “{0}” to the list “{1}”",
        ["marcou «{0}» como comprado"] = "marked “{0}” as bought",
        ["tirou «{0}» da lista «{1}»"] = "removed “{0}” from the list “{1}”",
        ["limpou 1 artigo comprado da lista «{0}»"] = "cleared 1 bought item from the list “{0}”",
        ["limpou {0} artigos comprados da lista «{1}»"] = "cleared {0} bought items from the list “{1}”",

        // Ajuda e sugestões
        ["Já enviaste muitas mensagens hoje. Obrigado! Tenta outra vez amanhã."] =
            "You've sent a lot of messages today. Thank you! Try again tomorrow.",
        ["Só quem mantém o Kubico vê as mensagens recebidas."] = "Only the people who maintain Kubico can see received messages.",
    };

    private static readonly Dictionary<string, string> Exact =
        Catalog.Where(e => !Placeholder().IsMatch(e.Key)).ToDictionary(e => e.Key, e => e.Value);

    /// <summary>Os textos com partes variáveis, dos mais específicos (mais texto fixo) para os mais gerais.</summary>
    private static readonly Pattern[] Patterns =
    [
        .. Catalog.Where(e => Placeholder().IsMatch(e.Key))
            .Select(e => new Pattern(e.Key, e.Value))
            .OrderByDescending(p => p.FixedLength),
    ];

    [GeneratedRegex(@"\{(\*?)(\d+)\}")]
    private static partial Regex Placeholder();

    private sealed class Pattern
    {
        private readonly string _english;
        private readonly HashSet<int> _translated = [];

        public Pattern(string portuguese, string english)
        {
            _english = english;
            var regex = new StringBuilder("^");
            var last = 0;
            foreach (Match m in Placeholder().Matches(portuguese))
            {
                regex.Append(Regex.Escape(portuguese[last..m.Index]));
                var index = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                regex.Append($"(?<p{index}>.+?)");
                if (m.Groups[1].Value == "*")
                {
                    _translated.Add(index);
                }

                FixedLength += m.Index - last;
                last = m.Index + m.Length;
            }

            regex.Append(Regex.Escape(portuguese[last..])).Append('$');
            FixedLength += portuguese.Length - last;
            Regex = new Regex(regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }

        public Regex Regex { get; }

        public int FixedLength { get; }

        public string Format(Match match, string language) => Placeholder().Replace(_english, m =>
        {
            var index = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var value = match.Groups[$"p{index}"].Value;
            return _translated.Contains(index) ? Translate(value, language) : value;
        });
    }
}
