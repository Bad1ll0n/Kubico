using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Households;
using Kubico.Api.Meals;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Kubico.Domain.Meals;

namespace Kubico.Api.Tests;

/// <summary>Refeições e receitas (F4-09): permissões, plano, "pôr na lista", isolamento e modelos.</summary>
public class MealTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Adults_write_recipes_children_plan_meals_and_strangers_see_nothing()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var (stranger, _, _) = await Fase1.HouseholdAsync(factory);
        var soup = await NotificationTests.Post<RecipeDto>(admin, $"/api/households/{household}/recipes",
            new SaveRecipeRequest("Sopa", Ingredients: [new IngredientDto("Cenoura", "3", "Frutas e legumes")], Tags: ["Sopa"]));
        var day = factory.Today;

        var childRecipe = await child.PostAsJsonAsync($"/api/households/{household}/recipes", new SaveRecipeRequest("Gelado"), TestJson.Options);
        var childPlan = await child.PutAsJsonAsync($"/api/households/{household}/meals/{day:yyyy-MM-dd}/Dinner", new SaveMealRequest(soup.Id), TestJson.Options);
        var childSees = await child.GetFromJsonAsync<List<RecipeDto>>($"/api/households/{household}/recipes", TestJson.Options);
        var strangerRecipes = await stranger.GetAsync($"/api/households/{household}/recipes");
        var strangerPlan = await stranger.PutAsJsonAsync($"/api/households/{household}/meals/{day:yyyy-MM-dd}/Lunch", new SaveMealRequest(Text: "x"), TestJson.Options);
        var otherRecipe = await stranger.PutAsJsonAsync($"/api/households/{household}/recipes/{soup.Id}", new SaveRecipeRequest("Roubada"), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, childRecipe.StatusCode);
        Assert.Equal(HttpStatusCode.OK, childPlan.StatusCode);
        Assert.Single(childSees!);
        Assert.Equal(HttpStatusCode.NotFound, strangerRecipes.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerPlan.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherRecipe.StatusCode);
    }

    [Fact]
    public async Task The_plan_keeps_one_meal_per_slot_and_a_deleted_recipe_leaves_the_day_empty()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var pasta = await NotificationTests.Post<RecipeDto>(admin, $"/api/households/{household}/recipes", new SaveRecipeRequest("Massa"));
        var day = factory.Today;
        var url = $"/api/households/{household}/meals";

        var first = await admin.PutAsJsonAsync($"{url}/{day:yyyy-MM-dd}/Lunch", new SaveMealRequest(Text: "Restos"), TestJson.Options);
        var parallel = await Fase1.ParallelAsync(4, i => admin.PutAsJsonAsync($"{url}/{day:yyyy-MM-dd}/Dinner", new SaveMealRequest(pasta.Id), TestJson.Options));
        var week = await admin.GetFromJsonAsync<List<MealPlanEntryDto>>($"{url}?from={day:yyyy-MM-dd}&to={day.AddDays(6):yyyy-MM-dd}", TestJson.Options);
        (await admin.DeleteAsync($"/api/households/{household}/recipes/{pasta.Id}")).EnsureSuccessStatusCode();
        var after = await admin.GetFromJsonAsync<List<MealPlanEntryDto>>($"{url}?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}", TestJson.Options);
        var tooLong = await admin.GetAsync($"{url}?from={day:yyyy-MM-dd}&to={day.AddDays(40):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, parallel);
        Assert.Equal(2, week!.Count);
        Assert.Contains(week, m => m.Slot == MealSlot.Lunch && m.Text == "Restos");
        Assert.Contains(week, m => m.Slot == MealSlot.Dinner && m.RecipeId == pasta.Id);
        Assert.Null(after!.Single(m => m.Slot == MealSlot.Dinner).RecipeId);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task The_week_ingredients_go_to_the_list_merged_and_without_what_is_already_there()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{household}/lists", new CreateShoppingListRequest("Supermercado"));
        await NotificationTests.Post<ShoppingItemDto>(admin, $"/api/households/{household}/lists/{list.Id}/items", new AddShoppingItemRequest("Arroz"));
        var soup = await NotificationTests.Post<RecipeDto>(admin, $"/api/households/{household}/recipes", new SaveRecipeRequest(
            "Sopa", Ingredients: [new IngredientDto("Cebola", "1"), new IngredientDto("Cenoura", "3", "Frutas e legumes")]));
        var rice = await NotificationTests.Post<RecipeDto>(admin, $"/api/households/{household}/recipes", new SaveRecipeRequest(
            "Arroz de pato", Ingredients: [new IngredientDto("cebola ", "2"), new IngredientDto("Arroz", "400 g"), new IngredientDto("Pato", "1")]));
        var day = factory.Today;
        var url = $"/api/households/{household}/meals";
        (await admin.PutAsJsonAsync($"{url}/{day:yyyy-MM-dd}/Lunch", new SaveMealRequest(soup.Id), TestJson.Options)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{url}/{day.AddDays(1):yyyy-MM-dd}/Dinner", new SaveMealRequest(rice.Id), TestJson.Options)).EnsureSuccessStatusCode();

        var result = await NotificationTests.Post<MealsToListDto>(admin, $"{url}/to-list", new MealsToListRequest(day, day.AddDays(6), list.Id));
        var items = await admin.GetFromJsonAsync<List<ShoppingItemDto>>($"/api/households/{household}/lists/{list.Id}/items", TestJson.Options);

        Assert.Equal(3, result.Added);
        Assert.Equal(["Cebola", "Cenoura", "Pato"], result.Names);
        Assert.Equal("1 + 2", items!.Single(i => i.Name == "Cebola").Quantity);
        Assert.Equal("Frutas e legumes", items!.Single(i => i.Name == "Cenoura").Category);
        Assert.Single(items!, i => i.Name == "Arroz");
    }

    [Fact]
    public async Task A_household_from_a_template_starts_with_portuguese_recipes()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var household = await NotificationTests.Post<HouseholdDto>(admin, "/api/households",
            new CreateHouseholdRequest("Casa", Template: HouseholdTemplate.Couple));

        var recipes = await admin.GetFromJsonAsync<List<RecipeDto>>($"/api/households/{household.Id}/recipes", TestJson.Options);

        Assert.Equal(10, recipes!.Count);
        Assert.Contains(recipes, r => r.Title == "Bacalhau à Brás" && r.Ingredients.Any(i => i.Name == "Ovos"));
    }
}
