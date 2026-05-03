namespace SmartPantry

open System
open WebSharper

/// Heuristic guards on user-typed ingredient names. Catches the obvious
/// garbage (random keymashing, single chars, only digits, profanity, common
/// non-food words) so we don't waste LLM tokens later. Anything not in the
/// curated Catalog still passes — humans buy weird things.
[<JavaScript>]
module Validation =

    let private normalize (s: string) : string =
        if isNull s then ""
        else
            (s.ToLower())
                .Replace("á", "a").Replace("é", "e").Replace("í", "i")
                .Replace("ó", "o").Replace("ö", "o").Replace("ő", "o")
                .Replace("ú", "u").Replace("ü", "u").Replace("ű", "u")
                .Trim()

    /// Lowest-effort profanity list (EN + HU). Not exhaustive — just the
    /// most common attempts. We match on word boundaries via plain substring
    /// after normalisation; tightened against false positives by requiring
    /// a complete-word equality match.
    let private profanityBank =
        [
            // EN
            "fuck"; "shit"; "bitch"; "asshole"; "dick"; "cock"; "pussy"; "cunt"
            "bastard"; "bullshit"; "wanker"; "twat"; "fag"; "nigger"
            // HU
            "kurva"; "fasz"; "geci"; "picsa"; "anyad"; "anyád"; "buzi"; "köcsög"
            "kocsog"; "fasszopo"; "faszszopó"; "szar"; "rohadt"; "baszd"
        ]

    /// Common non-food words that people might test the box with.
    /// Conservative — only blocks things obviously not edible.
    let private nonFoodBank =
        [
            // English
            "car"; "bike"; "bicycle"; "wheel"; "computer"; "laptop"; "phone"
            "keyboard"; "mouse"; "shoe"; "shoes"; "shirt"; "table"; "chair"
            "screen"; "tv"; "television"; "rock"; "stone"; "wood"; "metal"
            "plastic"; "paper"; "money"; "dollar"; "euro"; "tire"; "tyre"
            // Hungarian
            "auto"; "autó"; "bicikli"; "kerek"; "kerék"; "szamitogep"
            "számítógép"; "telefon"; "billentyuzet"; "billentyűzet"; "eger"
            "egér"; "cipo"; "cipő"; "ing"; "asztal"; "szek"; "szék"; "kepernyo"
            "képernyő"; "tv"; "televizio"; "televízió"; "ko"; "kő"; "fa"
            "fem"; "fém"; "muanyag"; "műanyag"; "papir"; "papír"; "penz"; "pénz"
        ]

    type Reason =
        | TooShort
        | OnlyDigits
        | RepeatedChars
        | Profanity
        | NotFood

    /// Returns Ok trimmedName | Error reason.
    /// Caller picks a localised message via reasonText below.
    let validate (raw: string) : Result<string, Reason> =
        let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
        let n = normalize trimmed

        if n.Length < 2 then Error TooShort
        elif n |> Seq.forall Char.IsDigit then Error OnlyDigits
        else
            // Repeated single character or two-char alternation, e.g. "aaaaa", "asdasd"
            let isLowEntropy (s: string) =
                if s.Length < 4 then false
                else
                    let distinct = s |> Seq.distinct |> Seq.length
                    // 4+ chars but only 1 unique
                    if distinct <= 1 then true
                    // "asdasd" / "abab" patterns: very few unique chars relative to length
                    elif distinct = 2 && s.Length >= 4 then true
                    elif distinct <= 3 && s.Length >= 6 then true
                    else
                        // Check for a 2-3 char block that just repeats
                        let blockSizes = [ 2; 3 ]
                        blockSizes
                        |> List.exists (fun bs ->
                            if s.Length < bs * 2 then false
                            else
                                let head = s.Substring(0, bs)
                                let mutable ok = true
                                let mutable i = bs
                                while ok && i + bs <= s.Length do
                                    if s.Substring(i, bs) <> head then ok <- false
                                    i <- i + bs
                                ok)
            if isLowEntropy n then Error RepeatedChars
            elif profanityBank |> List.exists (fun w -> n = w) then Error Profanity
            elif nonFoodBank |> List.exists (fun w -> n = w) then Error NotFood
            else Ok trimmed

    /// Localized error message for a validation Reason.
    let reasonText (lang: Lang) (r: Reason) : string =
        match lang, r with
        | En, TooShort      -> "Name is too short."
        | En, OnlyDigits    -> "Name can't be only digits."
        | En, RepeatedChars -> "That doesn't look like a real ingredient."
        | En, Profanity     -> "Let's keep the pantry friendly, please."
        | En, NotFood       -> "I don't think that's food."
        | Hu, TooShort      -> "Túl rövid a név."
        | Hu, OnlyDigits    -> "A név nem állhat csak számokból."
        | Hu, RepeatedChars -> "Ez nem tűnik valódi alapanyagnak."
        | Hu, Profanity     -> "Maradjunk a kamránál és a kulturált szavaknál."
        | Hu, NotFood       -> "Ez nem hiszem, hogy étel."
