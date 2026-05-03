namespace SmartPantry

open System
open System.Net.Http
open Microsoft.AspNetCore.Http
open WebSharper

/// Cookie-based anonymous user identity. Read by RPC methods to scope queries
/// to a specific browser session. NEVER trust a UserId sent by the client over
/// RPC — always pull from the cookie set by middleware.
module UserContext =

    let cookieName = "sp_uid"

    /// Pulls the UserId off HttpContext.Items where the cookie middleware
    /// stashed it. Falls back to a fresh GUID for the unlikely race where the
    /// middleware did not run (e.g. RPC called outside an HTTP request).
    let currentUserId () : string =
        let ctx = Web.Remoting.GetContext()
        let httpCtx = ctx.Environment.["HttpContext"] :?> HttpContext
        match httpCtx.Items.TryGetValue("UserId") with
        | true, (:? string as uid) when not (String.IsNullOrEmpty uid) -> uid
        | _ ->
            // Fallback — should not happen in practice because middleware runs first.
            Guid.NewGuid().ToString("N")

    /// Pulls the IHttpClientFactory off DI to avoid HttpClient socket leaks.
    let getHttpClient () : HttpClient =
        let ctx = Web.Remoting.GetContext()
        let httpCtx = ctx.Environment.["HttpContext"] :?> HttpContext
        let factory =
            httpCtx.RequestServices.GetService(typeof<IHttpClientFactory>)
            :?> IHttpClientFactory
        factory.CreateClient("groq")


/// RPC surface exposed to the client. All methods return Async<'T> so WebSharper
/// can wire them straight to JS Promises.
module Server =

    [<Rpc>]
    let GetItems () : Async<PantryItem list> =
        async {
            let userId = UserContext.currentUserId ()
            return Database.getItems userId
        }

    [<Rpc>]
    let AddItem (input: PantryItemInput) : Async<PantryItem> =
        async {
            let userId = UserContext.currentUserId ()
            // Minimal server-side validation — defense in depth.
            let trimmed = (input.Name |> Option.ofObj |> Option.defaultValue "").Trim()
            if String.IsNullOrWhiteSpace(trimmed) then
                return failwith "Az alapanyag neve nem lehet üres."
            elif input.Quantity < 0.0 then
                return failwith "A mennyiség nem lehet negatív."
            else
                let safe = { input with Name = trimmed; Unit = (input.Unit |> Option.ofObj |> Option.defaultValue "db").Trim() }
                return Database.addItem userId safe
        }

    [<Rpc>]
    let DeleteItem (id: int) : Async<bool> =
        async {
            let userId = UserContext.currentUserId ()
            let rows = Database.deleteItem userId id
            return rows > 0
        }

    [<Rpc>]
    let UpdateItem (item: PantryItem) : Async<bool> =
        async {
            let userId = UserContext.currentUserId ()
            // Force the server-side userId regardless of what the client sent.
            let safe = { item with UserId = userId }
            let rows = Database.updateItem userId safe
            return rows > 0
        }

    [<Rpc>]
    let GenerateRecipe () : Async<Result<Recipe, string>> =
        async {
            let userId = UserContext.currentUserId ()
            let items = Database.getItems userId
            if List.isEmpty items then
                return Error "A kamra üres — adj hozzá legalább egy alapanyagot először."
            else
                let httpClient = UserContext.getHttpClient ()
                let! r = LlmClient.generateRecipeAsync httpClient items |> Async.AwaitTask
                return r
        }
