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

                // 1) Harvest real-world inspiration recipes from TheMealDB
                //    (filtered by 1–3 of the user's English-mapped ingredients).
                let! inspirations =
                    MealDbClient.collectInspirations httpClient items
                    |> Async.AwaitTask
                let inspirationTitles =
                    inspirations |> List.map (fun i -> i.Title)

                // 2) Hand the inspirations to Groq as style references.
                let! groqResult =
                    LlmClient.generateRecipesAsync httpClient lang inspirationTitles items
                    |> Async.AwaitTask

                match groqResult with
                | Error e -> return Error e
                | Ok bundle ->
                    // 3) For each Groq-generated recipe, resolve a real
                    //    image: prefer an exact match against the
                    //    inspiration list (case-insensitive); else search
                    //    TheMealDB by the recipe title.
                    let inspirationMap =
                        inspirations
                        |> List.map (fun i -> i.Title.ToLower(), i.ImageUrl)
                        |> Map.ofList

                    let resolveImage (recipe: Recipe) : System.Threading.Tasks.Task<string> =
                        task {
                            // Try exact lower-case match against inspiration list (works
                            // when the LLM literally riffed on a real recipe name).
                            let titleKey = recipe.Title.ToLower()
                            match Map.tryFind titleKey inspirationMap with
                            | Some url when not (System.String.IsNullOrEmpty url) ->
                                return url
                            | _ ->
                                // Search TheMealDB by the ENGLISH ImagePromptHint (e.g.
                                // "creamy mushroom risotto") — the localized recipe
                                // title may be Hungarian and would never match.
                                let queryHint =
                                    if System.String.IsNullOrWhiteSpace recipe.ImagePromptHint
                                    then recipe.Title
                                    else recipe.ImagePromptHint
                                let! hitByHint =
                                    MealDbClient.searchByName httpClient queryHint
                                match hitByHint with
                                | Some h when not (System.String.IsNullOrEmpty h.ImageUrl) ->
                                    return h.ImageUrl
                                | _ ->
                                    // Final fallback: if still no hit, try the title
                                    // verbatim — covers the EN-mode case where the
                                    // hint and title are both English.
                                    let! hitByTitle =
                                        MealDbClient.searchByName httpClient recipe.Title
                                    return
                                        match hitByTitle with
                                        | Some h -> h.ImageUrl
                                        | None -> ""
                        }

                    // Run image lookups in parallel.
                    let lookupTasks =
                        bundle.Recipes
                        |> List.map (fun r -> resolveImage r)
                        |> Array.ofList
                    let! urls =
                        System.Threading.Tasks.Task.WhenAll(lookupTasks)
                        |> Async.AwaitTask

                    let enrichedRecipes =
                        bundle.Recipes
                        |> List.mapi (fun i r ->
                            let resolved = if i < urls.Length then urls.[i] else ""
                            { r with ImageUrl = resolved })

                    return Ok ({ Recipes = enrichedRecipes } : RecipeBundle)
        }
