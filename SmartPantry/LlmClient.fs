namespace SmartPantry

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

/// OpenAI Chat Completions client. The wire format is identical to Groq's
/// OpenAI-compatible endpoint, so swapping providers is just an endpoint +
/// model + env-var change. Override the model via OPENAI_MODEL.
/// Documentation: https://platform.openai.com/docs/api-reference/chat
module LlmClient =

    let private endpoint = "https://api.openai.com/v1/chat/completions"

    /// Default model — gpt-5.4-mini. Cheap (~$0.75 input / $4.50 output per
    /// 1M tokens) but smart enough for structured JSON recipe generation.
    /// Override with the OPENAI_MODEL env var if you want a different one
    /// (e.g. gpt-5.4 for higher quality, gpt-5.4-nano for cheaper).
    let private model () =
        match Environment.GetEnvironmentVariable("OPENAI_MODEL") with
        | null | "" -> "gpt-5.4-mini"
        | m -> m

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
    /// `inspirations` are real recipe titles (English) we lifted from
    /// TheMealDB to nudge Groq toward authentic, well-formed recipes.
    let buildPrompt (lang: Lang) (inspirations: string list) (items: PantryItem list) : string =
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

        let inspirationBlock =
            if List.isEmpty inspirations then []
            else [
                ""
                "INSPIRATION — real-world recipes from a culinary database that use similar ingredients. Use them as STYLE references; do NOT copy a title verbatim, riff on them:"
                inspirations |> List.map (sprintf "  • %s") |> String.concat "\n"
            ]

        let lines = [
            yield sprintf "TARGET LANGUAGE: %s. %s" langName langDirective
            yield ""
            yield "You are a creative chef. The user has these ingredients in their pantry:"
            yield inventory
            yield! inspirationBlock
            yield ""
            yield "TASK: Suggest EXACTLY 3 distinct recipes using the user's pantry ingredients (especially the ones expiring soon)."
            yield "Make them noticeably different in style: recipe #1 = QUICK & light, #2 = HEARTY & comforting, #3 = CREATIVE & playful."
            yield "Assume basic seasonings (salt, pepper, oil, water) are always available."
            yield ""
            yield "RESPOND STRICTLY in this JSON format, NOTHING ELSE — no Markdown, no commentary:"
            yield "{"
            yield "  \"recipes\": ["
            yield "    {"
            yield sprintf "      \"title\": \"%s\"," exampleTitle
            yield "      \"prepTimeMinutes\": 25,"
            yield "      \"steps\": ["
            yield sprintf "        {\"stepNumber\": 1, \"instruction\": \"%s\"}," exampleStep
            yield "        {\"stepNumber\": 2, \"instruction\": \"…\"}"
            yield "      ],"
            yield sprintf "      \"tags\": %s," exampleTags
            yield "      \"imagePromptHint\": \"short ENGLISH phrase (always English!) describing the finished dish on a plate\""
            yield "    }, ... two more recipes ..."
            yield "  ]"
            yield "}"
            yield ""
            yield sprintf "REMINDER: title, instruction, tags MUST be in %s. Only imagePromptHint is in ENGLISH (it feeds an image generator)." langName
        ]
        String.concat "\n" lines

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// Generate up to 3 recipe alternatives from a pantry. Returns Ok bundle
    /// or Error human-readable message in the requested language. Inspirations
    /// are real-world recipe titles harvested from TheMealDB to nudge Groq
    /// toward authentic cuisine; pass an empty list for no inspiration.
    let generateRecipesAsync (httpClient: HttpClient) (lang: Lang)
                             (inspirations: string list) (items: PantryItem list)
                             : Task<Result<RecipeBundle, string>> =
        task {
            let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            if String.IsNullOrWhiteSpace(apiKey) then
                let msg =
                    match lang with
                    | En -> "Missing OPENAI_API_KEY environment variable. Set it in the .env file."
                    | Hu -> "Hiányzó OPENAI_API_KEY környezeti változó. Állítsd be a .env fájlban."
                return Error msg
            else
                try
                    let prompt = buildPrompt lang inspirations items
                    let systemMsg =
                        match lang with
                        | En ->
                            "You return STRICTLY valid JSON, no Markdown, no commentary. Recipe content (title, steps, tags) MUST be written in ENGLISH only — translate any non-English ingredient names into English in the recipe text."
                        | Hu ->
                            "Csak SZIGORÚAN érvényes JSON-t adsz vissza, sem Markdown, sem kommentár. A recept tartalma (cím, lépések, címkék) KIZÁRÓLAG MAGYARUL íródjon — az idegen nevű alapanyagokat fordítsd magyarra a lépésekben."
                    let req = {
                        Model = model ()
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
                                        // Server fills this in afterwards via TheMealDB lookup.
                                        ImageUrl = ""
                                    }
                                let recipes : Recipe list =
                                    (if isNull wire.Recipes then Array.empty else wire.Recipes)
                                    |> Array.map convertWire
                                    |> List.ofArray
                                if List.isEmpty recipes then
                                    let m =
                                        match lang with
                                        | En -> "Couldn't come up with anything tasty from these ingredients. Try adding a few more recognisable items and ask again."
                                        | Hu -> "Ezekből az alapanyagokból nem született ötlet. Próbálj még pár felismerhető hozzávalót, és kérj receptet újra."
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
