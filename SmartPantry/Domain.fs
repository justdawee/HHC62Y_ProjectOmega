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

/// A single recipe variant. The `ImagePromptHint` is a short English phrase
/// suitable for handing to an image generator — kept separate from the
/// localized `Title` so we never feed Hungarian recipe names into a
/// stock-photo / diffusion service.
[<JavaScript; CLIMutable>]
type Recipe = {
    Title: string
    PrepTimeMinutes: int
    Steps: RecipeStep list
    Tags: string list
    ImagePromptHint: string
}

/// Bundle of alternative recipes the LLM proposes for a given pantry.
[<JavaScript; CLIMutable>]
type RecipeBundle = {
    Recipes: Recipe list
}
