namespace SmartPantry

open System
open WebSharper

/// A single ingredient record in a user's pantry. Persisted to SQLite, also serialized
/// over the WebSharper RPC boundary, so the type must round-trip safely.
[<JavaScript>]
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
[<JavaScript>]
type PantryItemInput = {
    Name: string
    Quantity: float
    Unit: string
    ExpiryDate: DateTime option
}

/// One step of an LLM-generated recipe.
[<JavaScript>]
type RecipeStep = {
    StepNumber: int
    Instruction: string
}

/// Final structured recipe returned to the client.
[<JavaScript>]
type Recipe = {
    Title: string
    PrepTimeMinutes: int
    Steps: RecipeStep list
    Tags: string list
}
