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

    /// Pulls the HttpContext for the current RPC request from WebSharper's context env.
    /// WebSharper.AspNetCore stashes the HttpContext under the literal key
    /// "WebSharper.AspNetCore.HttpContext"; older docs sometimes show "HttpContext".
    /// We try a few candidates so the code is resilient to either convention,
    /// and fall back to scanning the dict for any HttpContext value.
    let private httpContext () : HttpContext =
        let ctx = Web.Remoting.GetContext()
        let candidates = [
            "WebSharper.AspNetCore.HttpContext"
            "HttpContext"
            "Microsoft.AspNetCore.Http.HttpContext"
        ]
        let tryKey (k: string) =
            let mutable boxed : obj = null
            if ctx.Environment.TryGetValue(k, &boxed) && (boxed :? HttpContext)
            then Some (boxed :?> HttpContext)
            else None
        candidates
        |> List.tryPick tryKey
        |> Option.defaultWith (fun () ->
            let any =
                ctx.Environment
                |> Seq.tryPick (fun kvp ->
                    if kvp.Value :? HttpContext then Some (kvp.Value :?> HttpContext) else None)
            match any with
            | Some h -> h
            | None ->
                let keys = ctx.Environment |> Seq.map (fun kvp -> kvp.Key) |> String.concat ", "
                failwithf "No HttpContext in WebSharper Environment. Known keys: %s" keys)

    /// Resolves the user identity straight from the request cookie. The
    /// middleware guarantees the cookie is set before any RPC handler runs.
    /// We avoid HttpContext.Items because F#'s dict-extension wraps keys in
    /// StructBox and clashes with the underlying IDictionary<object,object?>.
    let currentUserId () : string =
        let http = httpContext ()
        let mutable v = ""
        if http.Request.Cookies.TryGetValue(cookieName, &v) && not (String.IsNullOrWhiteSpace v) then
            v
        else
            // Fallback — should not happen because middleware appends the cookie
            // on the response and subsequent RPC posts include it.
            Guid.NewGuid().ToString("N")

    /// Pulls the IHttpClientFactory off DI to avoid HttpClient socket leaks.
    let getHttpClient () : HttpClient =
        let http = httpContext ()
        let factory =
            http.RequestServices.GetService(typeof<IHttpClientFactory>)
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
    let DeleteAll () : Async<int> =
        async {
            let userId = UserContext.currentUserId ()
            return Database.deleteAll userId
        }

    /// Returns the configured Pollinations.ai token (if any) so the client can
    /// append it to image URLs. The token is set via the POLLINATIONS_TOKEN env
    /// var and never embedded in the source. Empty string when not configured —
    /// in that case the client uses the free public tier.
    [<Rpc>]
    let GetImageToken () : Async<string> =
        async {
            let t = Environment.GetEnvironmentVariable("POLLINATIONS_TOKEN")
            return if isNull t then "" else t
        }

    [<Rpc>]
    let GenerateRecipes (lang: Lang) : Async<Result<RecipeBundle, string>> =
        async {
            let userId = UserContext.currentUserId ()
            let items = Database.getItems userId
            if List.isEmpty items then
                let msg =
                    match lang with
                    | En -> "Pantry is empty — add at least one ingredient first."
                    | Hu -> "A kamra üres — adj hozzá legalább egy alapanyagot először."
                return Error msg
            else
                let httpClient = UserContext.getHttpClient ()
                let! r = LlmClient.generateRecipesAsync httpClient lang items |> Async.AwaitTask
                return r
        }
