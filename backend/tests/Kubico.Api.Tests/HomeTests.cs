using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kubico.Api.Home;
using Kubico.Api.Notifications;
using Kubico.Domain.Home;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>A área Casa (F4-07, F4-08, F4-12, F4-13): equipamentos, cofre, ficheiros, recados, contactos e Portugal.</summary>
public class HomeTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Theory]
    [InlineData("2026-11-03", "2027-05-01", "2028-05-10")]
    [InlineData("2026-05-05", "2026-05-01", "2026-05-10")]
    [InlineData("2030-01-01", "2030-05-01", "2030-05-10")]
    public void Road_tax_is_due_in_the_registration_month_and_inspections_follow_the_4_2_2_1_rule(
        string today, string iuc, string inspection)
    {
        var registered = new DateOnly(2020, 5, 10);
        var day = DateOnly.Parse(today);

        Assert.Equal(DateOnly.Parse(iuc), PortugalCalendar.NextIuc(registered, day));
        Assert.Equal(DateOnly.Parse(inspection), PortugalCalendar.NextInspection(registered, day));
    }

    [Fact]
    public async Task Document_numbers_are_secret_to_their_owner_and_managers_and_strangers_get_404()
    {
        var (mum, auth, household) = await Fase1.HouseholdAsync(factory);
        var (dad, dadId) = await Fase1.MemberAsync(factory, mum, household, "Pai", HouseholdRole.Adult);
        var (guest, guestId) = await Fase1.MemberAsync(factory, mum, household, "Rui", HouseholdRole.Guest);
        var (stranger, _, _) = await Fase1.HouseholdAsync(factory);
        var url = $"/api/households/{household}/home";

        await NotificationTests.Post<DocumentDto>(guest, $"{url}/documents",
            new SaveDocumentRequest(DocumentKind.CitizenCard, "Cartão do Rui", guestId, "12345678", factory.Today.AddYears(3)));
        await NotificationTests.Post<DocumentDto>(mum, $"{url}/documents",
            new SaveDocumentRequest(DocumentKind.Passport, "Passaporte da Mãe", auth.User.Id, "P999"));
        var guestForOther = await guest.PostAsJsonAsync($"{url}/documents",
            new SaveDocumentRequest(DocumentKind.Passport, "Do Pai", dadId, "X"), TestJson.Options);

        var asGuest = await guest.GetFromJsonAsync<HomeDto>(url, TestJson.Options);
        var asDad = await dad.GetFromJsonAsync<HomeDto>(url, TestJson.Options);
        var strangerSees = await stranger.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, guestForOther.StatusCode);
        Assert.Equal("12345678", Assert.Single(asGuest!.Documents).Number);
        Assert.Equal(2, asDad!.Documents.Count);
        Assert.Contains(asDad.Documents, d => d.Name == "Passaporte da Mãe" && d.Number == "P999");
        Assert.Equal(HttpStatusCode.NotFound, strangerSees.StatusCode);
    }

    [Fact]
    public async Task Appliance_files_are_pdfs_or_images_and_a_car_shows_its_road_tax_and_inspection()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, mum, household, "Rita");
        var url = $"/api/households/{household}/home";
        var car = await NotificationTests.Post<ApplianceDto>(mum, $"{url}/appliances", new SaveApplianceRequest(
            "Carro", ApplianceKind.Car, Plate: "aa-00-bb", FirstRegistration: new DateOnly(2020, 5, 10),
            Maintenance: [new SaveMaintenanceRequest("Revisão", 365)]));

        using var pdf = new MultipartFormDataContent { { new ByteArrayContent("%PDF-1.4 fatura"u8.ToArray()), "file", "fatura.pdf" } };
        var uploaded = await mum.PostAsync($"{url}/files?ownerId={car.Id}", pdf);
        using var text = new MultipartFormDataContent { { new ByteArrayContent("isto não"u8.ToArray()), "file", "x.txt" } };
        var notAFile = await mum.PostAsync($"{url}/files?ownerId={car.Id}", text);
        using var childPdf = new MultipartFormDataContent { { new ByteArrayContent("%PDF-1.4"u8.ToArray()), "file", "a.pdf" } };
        var childUpload = await child.PostAsync($"{url}/files?ownerId={car.Id}", childPdf);
        var home = await child.GetFromJsonAsync<HomeDto>(url, TestJson.Options);
        var file = Assert.Single(Assert.Single(home!.Appliances).Files);
        var download = await child.GetAsync($"{url}/files/{file.Id}");

        Assert.Equal("AA-00-BB", car.Plate);
        Assert.NotNull(car.NextIuc);
        Assert.NotNull(car.NextInspection);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAFile.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, childUpload.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Everyone_leaves_notes_but_only_removes_their_own()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, mum, household, "Rita");
        var url = $"/api/households/{household}/home";

        var mumsNote = await NotificationTests.Post<NoteDto>(mum, $"{url}/notes", new SaveNoteRequest("Hoje há jantar fora"));
        var childNote = await NotificationTests.Post<NoteDto>(child, $"{url}/notes", new SaveNoteRequest("Preciso de cartolina"));
        var childDeletesMums = await child.DeleteAsync($"{url}/notes/{mumsNote.Id}");
        var childDeletesOwn = await child.DeleteAsync($"{url}/notes/{childNote.Id}");
        var expired = await NotificationTests.Post<NoteDto>(mum, $"{url}/notes", new SaveNoteRequest("Ontem", factory.Today.AddDays(-1)));
        var home = await mum.GetFromJsonAsync<HomeDto>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, childDeletesMums.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, childDeletesOwn.StatusCode);
        Assert.Equal("Hoje há jantar fora", Assert.Single(home!.Notes).Text);
        _ = expired;
    }
}

/// <summary>Os avisos da Casa às 9h (avança o relógio: servidor próprio).</summary>
public sealed class HomeReminderTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task On_the_first_of_february_the_household_hears_about_irs_road_tax_warranty_documents_and_birthdays()
    {
        var (mum, auth, household) = await Fase1.HouseholdAsync(_factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(_factory, mum, household, "Rita");
        var url = $"/api/households/{household}/home";
        var day = new DateOnly(2027, 2, 1);
        await NotificationTests.Post<ApplianceDto>(mum, $"{url}/appliances", new SaveApplianceRequest(
            "Carro", ApplianceKind.Car, FirstRegistration: new DateOnly(2019, 2, 20)));
        await NotificationTests.Post<ApplianceDto>(mum, $"{url}/appliances", new SaveApplianceRequest(
            "Máquina da loiça", WarrantyUntil: day.AddDays(30)));
        await NotificationTests.Post<DocumentDto>(mum, $"{url}/documents", new SaveDocumentRequest(
            DocumentKind.Passport, "Passaporte da Mãe", auth.User.Id, ExpiresOn: day.AddDays(60)));
        await NotificationTests.Post<ContactDto>(mum, $"{url}/contacts", new SaveContactRequest("Avó", Birthday: new DateOnly(1950, 2, 2)));

        _factory.Clock.SetUtcNow(new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 2)), TimeSpan.Zero));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync(default);
        }

        await using var check = _factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<KubicoDbContext>();
        var titles = await db.Notifications.AsNoTracking()
            .Where(n => n.Kind == NotificationKind.HomeReminder)
            .Select(n => new { n.UserId, n.Title })
            .ToListAsync();
        var mine = titles.Where(t => t.UserId == auth.User.Id).Select(t => t.Title).ToList();

        Assert.Contains("IRS: valida as faturas no e-Fatura até 25 de fevereiro", mine);
        Assert.Contains("IUC de «Carro» para pagar este mês", mine);
        Assert.Contains("A garantia de «Máquina da loiça» acaba daqui a 30 dias", mine);
        Assert.Contains("«Passaporte da Mãe» caduca daqui a 60 dias", mine);
        Assert.Contains("Amanhã faz anos: Avó", mine);
        // A Rita vê a área, por isso sabe do aniversário; os avisos do carro e do IRS são para quem gere.
        Assert.Contains(titles, t => t.UserId != auth.User.Id && t.Title == "Amanhã faz anos: Avó");
        Assert.DoesNotContain(titles, t => t.UserId != auth.User.Id && t.Title.StartsWith("IRS"));
        _ = child;
    }

    public void Dispose() => _factory.Dispose();
}
