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

    /// Best-effort client IP for rate-limit keying. Trusts CF-Connecting-IP
    /// (Cloudflare) and X-Forwarded-For (other proxies) so that direct hits
    /// behind the proxy are still attributable; falls back to the TCP peer.
    /// Spoofing CF-Connecting-IP requires bypassing Cloudflare entirely, at
    /// which point the attacker shows up under a real RemoteIpAddress anyway.
    let currentClientIp () : string =
        let http = httpContext ()
        let tryHeader (name: string) =
            let mutable vs = Microsoft.Extensions.Primitives.StringValues.Empty
            if http.Request.Headers.TryGetValue(name, &vs) && vs.Count > 0 then
                let raw = vs.[0]
                if String.IsNullOrWhiteSpace raw then None
                else Some (raw.Split(',').[0].Trim())
            else None
        match tryHeader "CF-Connecting-IP" with
        | Some ip -> ip
        | None ->
            match tryHeader "X-Forwarded-For" with
            | Some ip -> ip
            | None ->
                match http.Connection.RemoteIpAddress with
                | null -> "unknown"
                | ip -> ip.ToString()

    /// Pulls the IHttpClientFactory off DI to avoid HttpClient socket leaks.
    let getHttpClient () : HttpClient =
        let http = httpContext ()
        let factory =
            http.RequestServices.GetService(typeof<IHttpClientFactory>)
            :?> IHttpClientFactory
        factory.CreateClient("openai")


/// RPC surface exposed to the client. All methods return Async<'T> so WebSharper
/// can wire them straight to JS Promises.
module Server =

    /// Maximum length of an ingredient name accepted by the server. Anything
    /// longer is almost certainly an attempt to balloon the LLM prompt.
    let private maxNameLength = 80

    /// Hard upper bound on items per user. Stops a single bored attacker
    /// from filling the SQLite DB and the OpenAI prompt with junk.
    let private maxItemsPerUser = 200

    /// Cap on items forwarded to the LLM in a single GenerateRecipes call.
    /// The pantry can hold more (up to maxItemsPerUser); the prompt only
    /// gets the first slice so token cost is bounded.
    let private maxItemsToLlm = 30

    /// Highest plausible quantity. Anything past this is either a typo or
    /// a deliberate prompt-bloat attempt ("99999999999999999 kg flour").
    let private maxQuantity = 100_000.0

    /// Two-axis rate-limit keys: cookie identity (sp_uid) and client IP,
    /// checked in parallel. The cookie axis catches naive spammers; the
    /// IP axis catches the "fresh cookie per request" trick (the cookie
    /// middleware auto-mints a new uid for any request without one).
    let private cookieKey () = UserContext.currentUserId ()
    let private ipKey () = UserContext.currentClientIp ()

    /// Rate-limit gate for write-style operations. Failed checks throw so
    /// the WebSharper RPC pipeline turns it into a client-side exception
    /// (the UI's existing try/with surfaces the message inline). Write
    /// traffic is cheap, so a real human never trips this.
    let private guardWrite () =
        match RateLimiter.checkBoth "write" (cookieKey ()) (ipKey ())
                  RateLimiter.writePerCookie RateLimiter.writePerIp with
        | RateLimiter.Allowed -> ()
        | RateLimiter.Denied retry ->
            failwithf "Túl sok kérés / Too many requests. Próbáld újra %d másodperc múlva." retry

    /// Rate-limit gate for read operations. We return an empty result
    /// instead of throwing so accidental reload-spam doesn't render an
    /// error in the UI; a real human would never hit the read cap.
    let private guardRead () : bool =
        match RateLimiter.checkBoth "read" (cookieKey ()) (ipKey ())
                  RateLimiter.readPerCookie RateLimiter.readPerIp with
        | RateLimiter.Allowed -> true
        | RateLimiter.Denied _ -> false

    /// Server-side defense-in-depth name validation. Mirrors the client
    /// validator (Validation.fs) and adds a hard length cap so the LLM
    /// prompt size stays bounded regardless of what the client sends.
    let private sanitizeName (raw: string) : Result<string, string> =
        let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
        if String.IsNullOrWhiteSpace trimmed then
            Error "Az alapanyag neve nem lehet üres."
        elif trimmed.Length > maxNameLength then
            Error (sprintf "Az alapanyag neve túl hosszú (max %d karakter)." maxNameLength)
        else
            match Validation.validate trimmed with
            | Ok cleaned -> Ok cleaned
            | Error reason ->
                // Server side we don't know the user's UI language, so we
                // ship a bilingual fallback. The client validator already
                // catches this in the user's preferred language for the
                // common case.
                Error (Validation.reasonText Hu reason)

    let private sanitizeUnit (raw: string) : string =
        let u = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
        if String.IsNullOrWhiteSpace u then "db"
        elif u.Length > 16 then u.Substring(0, 16)
        else u

    let private sanitizeQuantity (q: float) : Result<float, string> =
        if Double.IsNaN q || Double.IsInfinity q then
            Error "Érvénytelen mennyiség."
        elif q < 0.0 then
            Error "A mennyiség nem lehet negatív."
        elif q > maxQuantity then
            Error (sprintf "A mennyiség túl nagy (max %g)." maxQuantity)
        else Ok q

    [<Rpc>]
    let GetItems () : Async<PantryItem list> =
        async {
            if not (guardRead ()) then
                // Spammer — quietly return empty. A real user never sees this.
                return []
            else
                let userId = UserContext.currentUserId ()
                return Database.getItems userId
        }

    [<Rpc>]
    let AddItem (input: PantryItemInput) : Async<PantryItem> =
        async {
            guardWrite ()
            let userId = UserContext.currentUserId ()

            // Enforce per-user item ceiling BEFORE we hit the validator so
            // a flood of bad-name posts can't probe the validator either.
            let existing = Database.getItems userId
            if List.length existing >= maxItemsPerUser then
                return failwithf "A kamra már megtelt (max %d tétel)." maxItemsPerUser
            else

            match sanitizeName input.Name with
            | Error msg -> return failwith msg
            | Ok cleanName ->
                match sanitizeQuantity input.Quantity with
                | Error msg -> return failwith msg
                | Ok q ->
                    let safe =
                        { input with
                            Name = cleanName
                            Quantity = q
                            Unit = sanitizeUnit input.Unit }
                    return Database.addItem userId safe
        }

    [<Rpc>]
    let DeleteItem (id: int) : Async<bool> =
        async {
            guardWrite ()
            let userId = UserContext.currentUserId ()
            let rows = Database.deleteItem userId id
            return rows > 0
        }

    [<Rpc>]
    let UpdateItem (item: PantryItem) : Async<bool> =
        async {
            guardWrite ()
            let userId = UserContext.currentUserId ()

            match sanitizeName item.Name with
            | Error msg -> return failwith msg
            | Ok cleanName ->
                match sanitizeQuantity item.Quantity with
                | Error msg -> return failwith msg
                | Ok q ->
                    // Force the server-side userId regardless of what the client sent.
                    let safe =
                        { item with
                            UserId = userId
                            Name = cleanName
                            Quantity = q
                            Unit = sanitizeUnit item.Unit }
                    let rows = Database.updateItem userId safe
                    return rows > 0
        }

    [<Rpc>]
    let DeleteAll () : Async<int> =
        async {
            guardWrite ()
            let userId = UserContext.currentUserId ()
            return Database.deleteAll userId
        }

    [<Rpc>]
    let GenerateRecipes (lang: Lang) : Async<Result<RecipeBundle, string>> =
        async {
            // First gate: cheap rate-limit check. Recipes spend OpenAI
            // tokens, so the cap is the tightest of any RPC.
            match RateLimiter.checkBoth "recipes" (cookieKey ()) (ipKey ())
                      RateLimiter.recipesPerCookie RateLimiter.recipesPerIp with
            | RateLimiter.Denied retry ->
                let msg =
                    match lang with
                    | En ->
                        sprintf "Slow down — too many recipe requests. Try again in %d seconds." retry
                    | Hu ->
                        sprintf "Túl gyorsan kérsz recepteket. Próbáld újra %d másodperc múlva." retry
                return Error msg
            | RateLimiter.Allowed ->

            let userId = UserContext.currentUserId ()
            let items = Database.getItems userId
            if List.isEmpty items then
                let msg =
                    match lang with
                    | En -> "Pantry is empty — add at least one ingredient first."
                    | Hu -> "A kamra üres — kérlek vegyél fel előbb legalább egy hozzávalót."
                return Error msg
            else
                // Server-side safeguard: only feed items that look like real
                // food to the LLM. Catalog hits OR items that pass the
                // Validation heuristics qualify; everything else is dropped
                // BEFORE we burn tokens on it. (Previously we only checked
                // that *some* item was plausible, but still passed the full
                // list to the LLM — giant token-waste vector.)
                let plausible =
                    items
                    |> List.filter (fun it ->
                        match Catalog.findByName it.Name with
                        | Some _ -> true
                        | None ->
                            match Validation.validate it.Name with
                            | Ok _ -> true
                            | Error _ -> false)
                if List.isEmpty plausible then
                    let msg =
                        match lang with
                        | En -> "None of the pantry items look like real food. Add at least one recognisable ingredient before asking for a recipe."
                        | Hu -> "Egyik tétel sem tűnik valódi ételnek. Receptkéréshez vegyél fel legalább egy felismerhető hozzávalót."
                    return Error msg
                else
                // Extra hard cap on prompt size: even if a user has 200
                // legit items, the LLM only sees the first 30. Keeps token
                // cost predictable.
                let trimmed = plausible |> List.truncate maxItemsToLlm
                let httpClient = UserContext.getHttpClient ()

                // 1) Harvest real-world inspiration recipes from TheMealDB
                //    (filtered by 1–3 of the user's English-mapped ingredients).
                let! inspirations =
                    MealDbClient.collectInspirations httpClient trimmed
                    |> Async.AwaitTask
                let inspirationTitles =
                    inspirations |> List.map (fun i -> i.Title)

                // 2) Hand the inspirations to Groq as style references.
                let! groqResult =
                    LlmClient.generateRecipesAsync httpClient lang inspirationTitles trimmed
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

                    // Pre-extract a lookup of Catalog-mapped English ingredient
                    // names from each recipe's hint, used as a last-resort
                    // food-themed image fetch.
                    let inspirationsArr =
                        inspirations
                        |> List.filter (fun i -> not (System.String.IsNullOrEmpty i.ImageUrl))
                        |> Array.ofList

                    let resolveImage (idx: int) (recipe: Recipe) : System.Threading.Tasks.Task<string> =
                        task {
                            // 1) Exact title match in the inspiration list.
                            let titleKey = recipe.Title.ToLower()
                            match Map.tryFind titleKey inspirationMap with
                            | Some url when not (System.String.IsNullOrEmpty url) ->
                                return url
                            | _ ->
                            // 2) Search TheMealDB by the ENGLISH ImagePromptHint
                            //    (e.g. "creamy mushroom risotto"). The localized
                            //    recipe title may be Hungarian and would not match.
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
                            // 3) Try the title verbatim (covers EN-mode where hint and
                            //    title may both be English but differ).
                            let! hitByTitle =
                                MealDbClient.searchByName httpClient recipe.Title
                            match hitByTitle with
                            | Some h when not (System.String.IsNullOrEmpty h.ImageUrl) ->
                                return h.ImageUrl
                            | _ ->
                            // 4) Filter TheMealDB by the first Catalog-recognised
                            //    English ingredient mentioned in the hint. This
                            //    sometimes yields an unrelated dish but at least
                            //    a thematic photo (e.g. "mushroom" -> any mushroom dish).
                            let candidateWords =
                                queryHint.Split(
                                    [| ' '; ','; '-'; '/' |],
                                    System.StringSplitOptions.RemoveEmptyEntries)
                                |> Array.choose (fun w ->
                                    match Catalog.findByName w with
                                    | Some s -> Some s.En
                                    | None -> None)
                            let mutable filterHit = ""
                            let mutable wi = 0
                            while filterHit = "" && wi < candidateWords.Length do
                                let! batch =
                                    MealDbClient.filterByIngredient httpClient candidateWords.[wi]
                                    |> Async.AwaitTask
                                let firstWithImg =
                                    batch
                                    |> List.tryFind (fun i -> not (System.String.IsNullOrEmpty i.ImageUrl))
                                match firstWithImg with
                                | Some h -> filterHit <- h.ImageUrl
                                | None -> ()
                                wi <- wi + 1
                            if filterHit <> "" then
                                return filterHit
                            else
                            // 5) Absolute last resort: deterministically pick from the
                            //    already-fetched inspirations so every recipe always
                            //    shows SOME food photo. We pick by variant index so
                            //    each variant gets a different image when possible.
                            if inspirationsArr.Length = 0 then return ""
                            else
                                let pickIdx = idx % inspirationsArr.Length
                                return inspirationsArr.[pickIdx].ImageUrl
                        }

                    // Run image lookups in parallel; index lets the deterministic
                    // last-resort pick spread across variants.
                    let lookupTasks =
                        bundle.Recipes
                        |> List.mapi (fun i r -> resolveImage i r)
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
