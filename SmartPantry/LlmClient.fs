namespace SmartPantry

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

/// Groq API client (OpenAI-compatible chat completions endpoint with JSON mode).
/// Documentation: https://console.groq.com/docs/api-reference
module LlmClient =

    let private endpoint = "https://api.groq.com/openai/v1/chat/completions"

    /// Default model — Llama 3.3 70B versatile, generous free-tier quota.
    let private model = "llama-3.3-70b-versatile"

    let private jsonOpts =
        let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        o.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        o

    // ------------------------------------------------------------------
    // Wire types — kept public (System.Text.Json refuses non-public)
    // with explicit JsonPropertyName to lock the wire spelling regardless
    // of F#'s default PascalCase compilation of record fields.
    // ------------------------------------------------------------------

    [<CLIMutable>]
    type Message = {
        [<JsonPropertyName("role")>]    Role: string
        [<JsonPropertyName("content")>] Content: string
    }

    [<CLIMutable>]
    type ResponseFormat = {
        [<JsonPropertyName("type")>] Type: string
    }

    [<CLIMutable>]
    type CompletionRequest = {
        [<JsonPropertyName("model")>]           Model: string
        [<JsonPropertyName("messages")>]        Messages: Message array
        [<JsonPropertyName("temperature")>]     Temperature: float
        [<JsonPropertyName("response_format")>] ResponseFormat: ResponseFormat
    }

    [<CLIMutable>]
    type Choice = {
        [<JsonPropertyName("message")>] Message: Message
    }

    [<CLIMutable>]
    type CompletionResponse = {
        [<JsonPropertyName("choices")>] Choices: Choice array
    }

    [<CLIMutable>]
    type RecipeStepWire = {
        [<JsonPropertyName("stepNumber")>]  StepNumber: int
        [<JsonPropertyName("instruction")>] Instruction: string
    }

    [<CLIMutable>]
    type RecipeWire = {
        [<JsonPropertyName("title")>]           Title: string
        [<JsonPropertyName("prepTimeMinutes")>] PrepTimeMinutes: int
        [<JsonPropertyName("steps")>]           Steps: RecipeStepWire array
        [<JsonPropertyName("tags")>]            Tags: string array
        [<JsonPropertyName("imagePromptHint")>] ImagePromptHint: string
    }

    [<CLIMutable>]
    type RecipeBundleWire = {
        [<JsonPropertyName("recipes")>] Recipes: RecipeWire array
    }

    // ------------------------------------------------------------------
    // Prompt construction
    // ------------------------------------------------------------------

    let private formatItem (item: PantryItem) =
        let qty = sprintf "%g %s" item.Quantity item.Unit
        let expiry =
            match item.ExpiryDate with
            | Some d -> sprintf ", expires %s" (d.ToString("yyyy-MM-dd"))
            | None -> ""
        sprintf "- %s (%s%s)" item.Name qty expiry

    /// Builds the prompt in the requested target language. Image hint is
    /// always English so we can feed it straight to image-generation APIs.
    let buildPrompt (lang: Lang) (items: PantryItem list) : string =
        let inventory =
            if List.isEmpty items
            then "(empty pantry)"
            else items |> List.map formatItem |> String.concat "\n"

        // Heavy language enforcement — Llama 3 sometimes drifts back to the
        // language of the pantry items if not pinned hard. We open AND close
        // the prompt with the directive, give a target-language example, and
        // explicitly forbid translating ingredient names back to their
        // original language inside the recipe text.
        let langName, langDirective, exampleTitle, exampleStep, exampleTags =
            match lang with
            | En ->
                "ENGLISH",
                "ALL natural-language fields (title, steps, tags) MUST be in ENGLISH. Translate Hungarian/foreign ingredient names into English in the recipe text — for example 'liszt' → 'flour', 'tej' → 'milk', 'tojás' → 'egg'.",
                "Quick mushroom risotto",
                "Whisk together 200 g of flour, 1 egg, 100 ml of milk, and a pinch of salt.",
                "[\"quick\",\"vegetarian\"]"
            | Hu ->
                "MAGYAR (HUNGARIAN)",
                "MINDEN természetes-nyelvi mező (cím, lépések, címkék) MAGYARUL legyen. Az angol/idegen alapanyag-neveket fordítsd magyarra a lépésekben — pl. 'flour' → 'liszt', 'milk' → 'tej', 'egg' → 'tojás'.",
                "Gyors gombás rizottó",
                "Keverj össze 200 g lisztet, 1 tojást, 100 ml tejet és egy csipet sót.",
                "[\"gyors\",\"vegetáriánus\"]"

        let lines = [
            sprintf "TARGET LANGUAGE: %s. %s" langName langDirective
            ""
            "You are a creative chef. The user has these ingredients in their pantry:"
            inventory
            ""
            "TASK: Suggest EXACTLY 3 distinct recipes using these ingredients (especially the ones expiring soon)."
            "Make them noticeably different in style: recipe #1 = QUICK & light, #2 = HEARTY & comforting, #3 = CREATIVE & playful."
            "Assume basic seasonings (salt, pepper, oil, water) are always available."
            ""
            "RESPOND STRICTLY in this JSON format, NOTHING ELSE — no Markdown, no commentary:"
            "{"
            "  \"recipes\": ["
            "    {"
            sprintf "      \"title\": \"%s\"," exampleTitle
            "      \"prepTimeMinutes\": 25,"
            "      \"steps\": ["
            sprintf "        {\"stepNumber\": 1, \"instruction\": \"%s\"}," exampleStep
            "        {\"stepNumber\": 2, \"instruction\": \"…\"}"
            "      ],"
            sprintf "      \"tags\": %s," exampleTags
            "      \"imagePromptHint\": \"short ENGLISH phrase (always English!) describing the finished dish on a plate\""
            "    }, ... two more recipes ..."
            "  ]"
            "}"
            ""
            sprintf "REMINDER: title, instruction, tags MUST be in %s. Only imagePromptHint is in ENGLISH (it feeds an image generator)." langName
        ]
        String.concat "\n" lines

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// Generate up to 3 recipe alternatives from a pantry. Returns Ok bundle
    /// or Error human-readable message in the requested language.
    let generateRecipesAsync (httpClient: HttpClient) (lang: Lang) (items: PantryItem list)
                             : Task<Result<RecipeBundle, string>> =
        task {
            let apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY")
            if String.IsNullOrWhiteSpace(apiKey) then
                let msg =
                    match lang with
                    | En -> "Missing GROQ_API_KEY environment variable. Set it in the .env file."
                    | Hu -> "Hiányzó GROQ_API_KEY környezeti változó. Állítsd be a .env fájlban."
                return Error msg
            else
                try
                    let prompt = buildPrompt lang items
                    let systemMsg =
                        match lang with
                        | En ->
                            "You return STRICTLY valid JSON, no Markdown, no commentary. Recipe content (title, steps, tags) MUST be written in ENGLISH only — translate any non-English ingredient names into English in the recipe text."
                        | Hu ->
                            "Csak SZIGORÚAN érvényes JSON-t adsz vissza, sem Markdown, sem kommentár. A recept tartalma (cím, lépések, címkék) KIZÁRÓLAG MAGYARUL íródjon — az idegen nevű alapanyagokat fordítsd magyarra a lépésekben."
                    let req = {
                        Model = model
                        Temperature = 0.7
                        ResponseFormat = { Type = "json_object" }
                        Messages = [|
                            { Role = "system"; Content = systemMsg }
                            { Role = "user";   Content = prompt }
                        |]
                    }
                    use msg = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    msg.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)
                    msg.Content <- JsonContent.Create(req, options = jsonOpts)

                    use cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45.0))
                    use! resp = httpClient.SendAsync(msg, cts.Token)

                    if not resp.IsSuccessStatusCode then
                        let! body = resp.Content.ReadAsStringAsync()
                        let snippet =
                            if body.Length > 240 then body.Substring(0, 240) + "..." else body
                        let prefix =
                            match lang with
                            | En -> sprintf "Groq API error (%d)" (int resp.StatusCode)
                            | Hu -> sprintf "Groq API hiba (%d)" (int resp.StatusCode)
                        return Error (sprintf "%s: %s" prefix snippet)
                    else
                        let! completion = resp.Content.ReadFromJsonAsync<CompletionResponse>(jsonOpts)
                        let content =
                            completion.Choices
                            |> Array.tryHead
                            |> Option.map (fun c -> c.Message.Content)
                            |> Option.defaultValue ""
                        if String.IsNullOrWhiteSpace(content) then
                            let m =
                                match lang with
                                | En -> "Groq returned an empty response."
                                | Hu -> "A Groq üres választ adott."
                            return Error m
                        else
                            try
                                let wire = JsonSerializer.Deserialize<RecipeBundleWire>(content, jsonOpts)
                                let convertWire (r: RecipeWire) : Recipe =
                                    {
                                        Title = r.Title
                                        PrepTimeMinutes = r.PrepTimeMinutes
                                        Steps =
                                            (if isNull r.Steps then Array.empty else r.Steps)
                                            |> Array.map (fun s ->
                                                ({ StepNumber = s.StepNumber
                                                   Instruction = s.Instruction } : RecipeStep))
                                            |> List.ofArray
                                        Tags =
                                            (if isNull r.Tags then Array.empty else r.Tags)
                                            |> List.ofArray
                                        ImagePromptHint =
                                            if isNull r.ImagePromptHint then r.Title
                                            else r.ImagePromptHint
                                    }
                                let recipes : Recipe list =
                                    (if isNull wire.Recipes then Array.empty else wire.Recipes)
                                    |> Array.map convertWire
                                    |> List.ofArray
                                if List.isEmpty recipes then
                                    let m =
                                        match lang with
                                        | En -> "The model returned no recipes — try again."
                                        | Hu -> "A modell nem adott vissza receptet — próbáld újra."
                                    return Error m
                                else
                                    return Ok ({ Recipes = recipes } : RecipeBundle)
                            with ex ->
                                let m =
                                    match lang with
                                    | En -> sprintf "Could not parse the recipe JSON: %s" ex.Message
                                    | Hu -> sprintf "Nem sikerült feldolgozni a recept JSON-t: %s" ex.Message
                                return Error m
                with
                | :? TaskCanceledException ->
                    let m =
                        match lang with
                        | En -> "Timeout: Groq did not respond within 45 seconds."
                        | Hu -> "Időtúllépés: a Groq nem válaszolt 45 másodpercen belül."
                    return Error m
                | ex ->
                    let m =
                        match lang with
                        | En -> sprintf "Network error: %s" ex.Message
                        | Hu -> sprintf "Hálózati hiba: %s" ex.Message
                    return Error m
        }
