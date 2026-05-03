namespace SmartPantry

open System
open WebSharper

/// A single ingredient record in a user's pantry. Persisted to SQLite, also serialized
/// over the WebSharper RPC boundary, so the type must round-trip safely.
[<JavaScript; CLIMutable>]
type PantryItem = {
    Id: int
    UserId: string
    Name: string
    Quantity: float
    Unit: string
    ExpiryDate: DateTime option
}

/// Data sent by the client when creating a new pantry item. UserId is added on the server
/// from the cookie — the client never gets to set it.
[<JavaScript; CLIMutable>]
type PantryItemInput = {
    Name: string
    Quantity: float
    Unit: string
    ExpiryDate: DateTime option
}

/// One step of an LLM-generated recipe.
[<JavaScript; CLIMutable>]
type RecipeStep = {
    StepNumber: int
    Instruction: string
}

/// A single recipe variant.
/// - `ImagePromptHint` is a short English phrase used as a fallback when we
///   could not match the recipe to a TheMealDB entry.
/// - `ImageUrl` is the photo URL the server resolved (typically TheMealDB).
///   Empty when no match was found — the client then renders a procedural
///   gradient + emoji decoration.
[<JavaScript; CLIMutable>]
type Recipe = {
    Title: string
    PrepTimeMinutes: int
    Steps: RecipeStep list
    Tags: string list
    ImagePromptHint: string
    ImageUrl: string
}

/// Bundle of alternative recipes the LLM proposes for a given pantry.
[<JavaScript; CLIMutable>]
type RecipeBundle = {
    Recipes: Recipe list
}
