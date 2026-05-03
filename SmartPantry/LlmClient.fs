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

        let langInstruction, exampleTitles, exampleTags =
            match lang with
            | En ->
                "Reply in English. Tone: warm, concise, like a friendly chef.",
                "[\"Quick mushroom risotto\", \"Hearty egg pancakes\", \"Creative parmesan croquettes\"]",
                "[\"quick\",\"vegetarian\"]"
            | Hu ->
                "Válaszolj magyarul. Hangulat: barátságos, lényegre törő séf-stílus.",
                "[\"Gyors gomba rizottó\", \"Laktató tojásos palacsinta\", \"Kreatív parmezános krokett\"]",
                "[\"gyors\",\"vegetariánus\"]"

        let lines = [
            "You are a creative chef. The user has these ingredients in their pantry:"
            inventory
            ""
            "Suggest EXACTLY 3 different recipes that use these ingredients (especially the soon-to-expire ones). Make them noticeably different in style — e.g., quick / hearty / creative."
            "Assume basic seasonings (salt, pepper, oil, water) are always available."
            langInstruction
            ""
            "RESPOND STRICTLY in this JSON format, NOTHING ELSE:"
            "{"
            "  \"recipes\": ["
            "    {"
            "      \"title\": \"Recipe name\","
            "      \"prepTimeMinutes\": 25,"
            "      \"steps\": ["
            "        {\"stepNumber\": 1, \"instruction\": \"Step one…\"},"
            "        {\"stepNumber\": 2, \"instruction\": \"Step two…\"}"
            "      ],"
            sprintf "      \"tags\": %s," exampleTags
            "      \"imagePromptHint\": \"short ENGLISH phrase describing the finished dish on a plate, suitable for an image generator\""
            "    }"
            "  ]"
            "}"
            ""
            sprintf "Example titles you might pick (do NOT copy literally): %s" exampleTitles
            "ALL imagePromptHint values must be in ENGLISH regardless of the rest of the response language, since they go to an image-generation API."
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
                        | En -> "You return STRICTLY valid JSON, never plain text. Reply in English."
                        | Hu -> "You return STRICTLY valid JSON, never plain text. Reply in Hungarian (magyar)."
                    let req = {
                        Model = model
                        Temperature = 0.85
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
