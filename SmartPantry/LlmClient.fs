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

    /// Default model — Llama 3.3 70B versatile, generous free-tier quota and ~500 tok/s.
    let private model = "llama-3.3-70b-versatile"

    let private jsonOpts =
        let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        o.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        o

    // ------------------------------------------------------------------
    // Request / response wire types (kept private to this module)
    // ------------------------------------------------------------------

    [<CLIMutable>]
    type private Message = {
        role: string
        content: string
    }

    [<CLIMutable>]
    type private ResponseFormat = {
        ``type``: string
    }

    [<CLIMutable>]
    type private CompletionRequest = {
        model: string
        messages: Message array
        temperature: float
        response_format: ResponseFormat
    }

    [<CLIMutable>]
    type private Choice = {
        message: Message
    }

    [<CLIMutable>]
    type private CompletionResponse = {
        choices: Choice array
    }

    [<CLIMutable>]
    type private RecipeStepWire = {
        stepNumber: int
        instruction: string
    }

    /// LLM-side recipe shape — strict JSON schema we ask Groq to return.
    [<CLIMutable>]
    type private RecipeWire = {
        title: string
        prepTimeMinutes: int
        steps: RecipeStepWire array
        tags: string array
    }

    // ------------------------------------------------------------------
    // Prompt construction
    // ------------------------------------------------------------------

    let private formatItem (item: PantryItem) =
        let qty = sprintf "%g %s" item.Quantity item.Unit
        let expiry =
            match item.ExpiryDate with
            | Some d -> sprintf ", lejár %s" (d.ToString("yyyy-MM-dd"))
            | None -> ""
        sprintf "- %s (%s%s)" item.Name qty expiry

    let buildPrompt (items: PantryItem list) : string =
        let inventory =
            if List.isEmpty items
            then "(üres kamra)"
            else items |> List.map formatItem |> String.concat "\n"
        let lines = [
            "Te egy magyar séf vagy. A felhasználónak ezek az alapanyagai vannak a kamrájában:"
            inventory
            ""
            "Adj egy gyors, max 30 perces, ízletes receptet, ami kihasználja a meglévő alapanyagokat (különösen a hamarosan lejárókat)."
            "Magyarul válaszolj. Ha kell, használj alap fűszereket (só, bors, olaj) — feltételezzük, hogy ezek mindenkinél megvannak."
            ""
            "SZIGORÚAN ebben a JSON formátumban válaszolj, MÁS SZÖVEG NÉLKÜL:"
            "{"
            "  \"title\": \"Recept neve\","
            "  \"prepTimeMinutes\": 25,"
            "  \"steps\": ["
            "    {\"stepNumber\": 1, \"instruction\": \"Első lépés...\"},"
            "    {\"stepNumber\": 2, \"instruction\": \"Második lépés...\"}"
            "  ],"
            "  \"tags\": [\"gyors\", \"vegetariánus\"]"
            "}"
        ]
        String.concat "\n" lines

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// Generate a recipe from a pantry. Returns Ok recipe / Error human-readable message.
    /// The HttpClient is supplied by DI (IHttpClientFactory) so we don't leak sockets.
    let generateRecipeAsync (httpClient: HttpClient) (items: PantryItem list)
                            : Task<Result<Recipe, string>> =
        task {
            let apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY")
            if String.IsNullOrWhiteSpace(apiKey) then
                return Error "Hiányzó GROQ_API_KEY környezeti változó. Állítsd be a .env fájlban."
            else
                try
                    let prompt = buildPrompt items
                    let req = {
                        model = model
                        temperature = 0.7
                        response_format = { ``type`` = "json_object" }
                        messages = [|
                            { role = "system"
                              content = "You return STRICTLY valid JSON, never plain text. The user is Hungarian; reply in Hungarian." }
                            { role = "user"; content = prompt }
                        |]
                    }
                    use msg = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    msg.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)
                    msg.Content <- JsonContent.Create(req, options = jsonOpts)

                    use cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! resp = httpClient.SendAsync(msg, cts.Token)

                    if not resp.IsSuccessStatusCode then
                        let! body = resp.Content.ReadAsStringAsync()
                        let snippet =
                            if body.Length > 240 then body.Substring(0, 240) + "..." else body
                        return Error (sprintf "Groq API hiba (%d): %s" (int resp.StatusCode) snippet)
                    else
                        let! completion = resp.Content.ReadFromJsonAsync<CompletionResponse>(jsonOpts)
                        let content =
                            completion.choices
                            |> Array.tryHead
                            |> Option.map (fun c -> c.message.content)
                            |> Option.defaultValue ""

                        if String.IsNullOrWhiteSpace(content) then
                            return Error "A Groq üres választ adott."
                        else
                            try
                                let wire = JsonSerializer.Deserialize<RecipeWire>(content, jsonOpts)
                                let recipe = {
                                    Title = wire.title
                                    PrepTimeMinutes = wire.prepTimeMinutes
                                    Steps =
                                        (if isNull wire.steps then Array.empty else wire.steps)
                                        |> Array.map (fun s ->
                                            { StepNumber = s.stepNumber
                                              Instruction = s.instruction })
                                        |> List.ofArray
                                    Tags =
                                        (if isNull wire.tags then Array.empty else wire.tags)
                                        |> List.ofArray
                                }
                                return Ok recipe
                            with ex ->
                                return Error (sprintf "Nem sikerült feldolgozni a recept JSON-t: %s" ex.Message)
                with
                | :? TaskCanceledException ->
                    return Error "Időtúllépés: a Groq nem válaszolt 30 másodpercen belül."
                | ex ->
                    return Error (sprintf "Hálózati hiba: %s" ex.Message)
        }
