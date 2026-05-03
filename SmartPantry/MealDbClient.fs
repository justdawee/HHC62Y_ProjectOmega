namespace SmartPantry

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

/// TheMealDB API client.
/// Free public test endpoint at /api/json/v1/1/...
/// Override the key with the MEALDB_KEY env var (paid supporter tier
/// unlocks higher rate limits and multi-ingredient filter).
module MealDbClient =

    /// Build the base URL. TheMealDB URL pattern is /api/json/v1/{key}/<endpoint>;
    /// the default test key is literally "1". MEALDB_KEY env var overrides it
    /// (paid supporter tier unlocks higher rate limits and multi-ingredient filter).
    let private base' () =
        let key =
            match Environment.GetEnvironmentVariable("MEALDB_KEY") with
            | null | "" -> "1"
            | k -> k
        sprintf "https://www.themealdb.com/api/json/v1/%s/" key

    let private jsonOpts =
        let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        o.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        o

    // ------------------------------------------------------------------
    // Wire types — TheMealDB returns lots of strXxx fields. We only care
    // about a small subset for the hybrid flow (inspiration titles +
    // per-title image lookup).
    // ------------------------------------------------------------------

    [<CLIMutable>]
    type MealHit = {
        [<JsonPropertyName("idMeal")>]      IdMeal: string
        [<JsonPropertyName("strMeal")>]     StrMeal: string
        [<JsonPropertyName("strMealThumb")>] StrMealThumb: string
    }

    [<CLIMutable>]
    type MealEnvelope = {
        [<JsonPropertyName("meals")>] Meals: MealHit array
    }

    /// One inspiration line we hand back to the prompt: a real-world recipe
    /// title + its thumbnail URL we can later use as the recipe image.
    type Inspiration = {
        Title: string
        ImageUrl: string
    }

    let private encode (s: string) : string =
        Uri.EscapeDataString(s)

    /// Run a single endpoint request and parse the meals envelope.
    let private fetchEnvelope (httpClient: HttpClient) (path: string) : Task<MealEnvelope> =
        task {
            try
                let url = base' () + path
                use! resp = httpClient.GetAsync(url)
                if resp.IsSuccessStatusCode then
                    let! env = resp.Content.ReadFromJsonAsync<MealEnvelope>(jsonOpts)
                    if box env = null then
                        return { Meals = Array.empty }
                    else
                        return env
                else
                    return { Meals = Array.empty }
            with _ ->
                return { Meals = Array.empty }
        }

    /// Filter meals by a single main ingredient (English).
    /// TheMealDB returns up to 100 hits with title + thumbnail only.
    let filterByIngredient (httpClient: HttpClient) (ingredient: string) : Task<Inspiration list> =
        task {
            let path = sprintf "filter.php?i=%s" (encode ingredient)
            let! env = fetchEnvelope httpClient path
            return
                (if isNull env.Meals then Array.empty else env.Meals)
                |> Array.choose (fun m ->
                    if String.IsNullOrWhiteSpace m.StrMeal then None
                    else
                        let img =
                            if isNull m.StrMealThumb then "" else m.StrMealThumb
                        Some { Title = m.StrMeal; ImageUrl = img })
                |> List.ofArray
        }

    /// Search meals by title — used to find an image for a Groq-generated
    /// recipe whose title may not exactly match a TheMealDB entry.
    let searchByName (httpClient: HttpClient) (title: string) : Task<Inspiration option> =
        task {
            // First try exact title search
            let path = sprintf "search.php?s=%s" (encode title)
            let! env = fetchEnvelope httpClient path
            let direct =
                (if isNull env.Meals then Array.empty else env.Meals)
                |> Array.tryHead
                |> Option.map (fun m ->
                    let img = if isNull m.StrMealThumb then "" else m.StrMealThumb
                    { Title = (if isNull m.StrMeal then title else m.StrMeal)
                      ImageUrl = img })
            match direct with
            | Some _ -> return direct
            | None ->
                // Fallback: pick the first significant word from the title
                // and try a fuzzier search. e.g. "Quick Mushroom Risotto" -> "Mushroom"
                let words =
                    title.Split([| ' '; '-'; ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.filter (fun w ->
                        w.Length >= 4
                        && not (Char.IsDigit(w.[0])))
                if words.Length = 0 then return None
                else
                    // Pick the longest word (more likely a content noun)
                    let candidate =
                        words |> Array.maxBy (fun w -> w.Length)
                    let path = sprintf "search.php?s=%s" (encode candidate)
                    let! env = fetchEnvelope httpClient path
                    return
                        (if isNull env.Meals then Array.empty else env.Meals)
                        |> Array.tryHead
                        |> Option.map (fun m ->
                            let img = if isNull m.StrMealThumb then "" else m.StrMealThumb
                            { Title = (if isNull m.StrMeal then title else m.StrMeal)
                              ImageUrl = img })
        }

    /// Gather an inspiration list of real recipes given a pantry. Picks 1-3
    /// of the user's ingredients (translated to English when possible via
    /// the catalog) and runs a filter for each, then takes a small spread.
    let collectInspirations (httpClient: HttpClient) (items: PantryItem list)
                            : Task<Inspiration list> =
        task {
            let englishNames =
                items
                |> List.choose (fun it ->
                    match Catalog.findByName it.Name with
                    | Some s -> Some s.En
                    | None ->
                        // If not in catalog, assume name is already in English
                        // (or close enough); only use ASCII-ish words.
                        let trimmed = (it.Name |> Option.ofObj |> Option.defaultValue "").Trim()
                        if trimmed.Length >= 3 then Some trimmed else None)
                |> List.distinct
                |> List.truncate 3
            if List.isEmpty englishNames then
                return []
            else
                let! batches =
                    englishNames
                    |> List.map (fun n -> filterByIngredient httpClient n)
                    |> Task.WhenAll
                // Merge, dedupe by title, keep at most 8 inspirations
                return
                    batches
                    |> Array.collect List.toArray
                    |> Array.distinctBy (fun i -> i.Title.ToLower())
                    |> Array.truncate 8
                    |> List.ofArray
        }
