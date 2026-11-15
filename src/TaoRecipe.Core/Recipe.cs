using System.Text.Json.Serialization;

namespace TaoRecipe.Core;

public sealed class RecipeExtraction
{
    [JsonPropertyName("source_language")] public string SourceLanguage { get; set; } = "";
    [JsonPropertyName("recipes")] public List<Recipe> Recipes { get; set; } = [];
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}

public sealed class Recipe
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("original_title")] public string OriginalTitle { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("cuisine")] public string Cuisine { get; set; } = "";
    [JsonPropertyName("course")] public string Course { get; set; } = "";
    [JsonPropertyName("servings")] public string Servings { get; set; } = "";
    [JsonPropertyName("prep_time")] public string PrepTime { get; set; } = "";
    [JsonPropertyName("cook_time")] public string CookTime { get; set; } = "";
    [JsonPropertyName("total_time")] public string TotalTime { get; set; } = "";
    [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("ingredient_groups")] public List<IngredientGroup> IngredientGroups { get; set; } = [];
    [JsonPropertyName("equipment")] public List<string> Equipment { get; set; } = [];
    [JsonPropertyName("steps")] public List<RecipeStep> Steps { get; set; } = [];
    [JsonPropertyName("tips")] public List<string> Tips { get; set; } = [];
    [JsonPropertyName("cover_timestamp_seconds")] public double CoverTimestampSeconds { get; set; }

    /// <summary>Local path of the cover screen grab (filled after extraction).</summary>
    [JsonPropertyName("cover_image")] public string? CoverImage { get; set; }
}

public sealed class IngredientGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ingredients")] public List<Ingredient> Ingredients { get; set; } = [];
}

public sealed class Ingredient
{
    [JsonPropertyName("quantity")] public string Quantity { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";

    public override string ToString()
    {
        var amount = string.Join(' ', new[] { Quantity, Unit }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var s = amount.Length > 0 ? $"{amount} {Name}" : Name;
        return string.IsNullOrWhiteSpace(Note) ? s : $"{s} ({Note})";
    }
}

public sealed class RecipeStep
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("instruction")] public string Instruction { get; set; } = "";
    [JsonPropertyName("timestamp_seconds")] public double TimestampSeconds { get; set; }
    [JsonPropertyName("has_visual")] public bool HasVisual { get; set; } = true;

    /// <summary>Local path of the step screen grab (filled after extraction).</summary>
    [JsonPropertyName("image")] public string? Image { get; set; }
}
