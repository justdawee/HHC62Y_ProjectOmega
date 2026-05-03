namespace SmartPantry

open WebSharper

/// UI language. Defaults to English; user can toggle to Hungarian.
[<JavaScript>]
type Lang =
    | En
    | Hu

/// Static UI string table — lookup by key, returns the localized text.
[<JavaScript>]
module Strings =

    type T = {
        // header
        Tagline: string
        IngredientsOnHand: string
        ThemeToggle: string
        LangToggle: string

        // pantry section
        YourPantry: string
        Items: string
        ClearAll: string
        ClearAllConfirm: string
        AddIngredientPlaceholder: string
        AddBtnLabel: string
        Pcs: string
        Grams: string
        Kg: string
        Ml: string
        Liter: string
        Cup: string
        Tbsp: string
        Tsp: string
        EmptyName: string
        NegativeQty: string
        InvalidQty: string

        // pantry empty state
        EmptyPantryTitle: string
        EmptyPantryHint: string

        // ai chef section
        AiChef: string
        ChefTagline: string
        GenerateRecipe: string
        IngredientsCount: string
        NoRecipeYet: string
        NoRecipeHintLine1: string
        NoRecipeHintLine2: string
        Thinking: string
        RetryBtn: string
        SomethingWrong: string

        // recipe display
        RecipeLabel: string
        Minutes: string
        AlternativesTitle: string
        Alt1: string
        Alt2: string
        Alt3: string
        PrevVariantAria: string
        NextVariantAria: string

        // language switch toast
        ToastTitle: string
        ToastBody: string
        ReloadLabel: string
        DismissLabel: string

        // expiry badges
        ExpiredDaysAgo: string
        ExpiresToday: string
        ExpiresInDays: string
        FreshUntil: string

        // footer — composed from two pieces with a clickable link to the
        // author's GitHub between them.
        FooterPrefix: string
        FooterAuthor: string
    }

    let en: T = {
        Tagline = "PANTRY · AI CHEF"
        IngredientsOnHand = "ingredients on hand"
        ThemeToggle = "Toggle theme"
        LangToggle = "Switch language"

        YourPantry = "Your Pantry"
        Items = "items"
        ClearAll = "Clear all"
        ClearAllConfirm = "Empty the whole pantry?"
        AddIngredientPlaceholder = "Add an ingredient…"
        AddBtnLabel = "Add ingredient"
        Pcs = "pcs"
        Grams = "g"
        Kg = "kg"
        Ml = "ml"
        Liter = "l"
        Cup = "cup"
        Tbsp = "tbsp"
        Tsp = "tsp"
        EmptyName = "Give the ingredient a name."
        NegativeQty = "Quantity cannot be negative."
        InvalidQty = "Invalid quantity."

        EmptyPantryTitle = "Your pantry is empty"
        EmptyPantryHint = "Add an ingredient — then I can suggest a recipe."

        AiChef = "AI Chef"
        ChefTagline = "turns ingredients into ideas"
        GenerateRecipe = "Generate Recipe with AI"
        IngredientsCount = "ingredients"
        NoRecipeYet = "No recipe yet"
        NoRecipeHintLine1 = "Stock the pantry, then hit the button above."
        NoRecipeHintLine2 = "Your recipe will materialize right here."
        Thinking = "The chef is thinking…"
        RetryBtn = "Try again"
        SomethingWrong = "Something went wrong"

        RecipeLabel = "RECIPE"
        Minutes = "min"
        AlternativesTitle = "Pick a variation"
        Alt1 = "Quick"
        Alt2 = "Hearty"
        Alt3 = "Creative"
        PrevVariantAria = "Previous variant"
        NextVariantAria = "Next variant"

        ToastTitle = "Language switched"
        ToastBody = "Existing pantry items and the current recipe stay in their original language. Reload to refetch in English."
        ReloadLabel = "Reload"
        DismissLabel = "Dismiss"

        ExpiredDaysAgo = "Expired %d days ago"
        ExpiresToday = "Expires today"
        ExpiresInDays = "Expires in %d days"
        FreshUntil = "Fresh · %s"

        FooterPrefix = "SmartPantry · pantry, refined · built with ♥ by"
        FooterAuthor = "JustDawee"
    }

    let hu: T = {
        Tagline = "KAMRA · AI SÉF"
        IngredientsOnHand = "alapanyag a kamrában"
        ThemeToggle = "Téma váltás"
        LangToggle = "Nyelv váltás"

        YourPantry = "A kamrád"
        Items = "elem"
        ClearAll = "Üríts mindent"
        ClearAllConfirm = "Tényleg ürítsem az egész kamrát?"
        AddIngredientPlaceholder = "Új alapanyag…"
        AddBtnLabel = "Alapanyag hozzáadása"
        Pcs = "db"
        Grams = "g"
        Kg = "kg"
        Ml = "ml"
        Liter = "l"
        Cup = "csésze"
        Tbsp = "ek"
        Tsp = "tk"
        EmptyName = "Adj nevet az alapanyagnak."
        NegativeQty = "A mennyiség nem lehet negatív."
        InvalidQty = "Érvénytelen mennyiség."

        EmptyPantryTitle = "A kamrád üres"
        EmptyPantryHint = "Adj hozzá egy alapanyagot — utána javasolhatok hozzá receptet."

        AiChef = "AI Séf"
        ChefTagline = "alapanyagból ötlet"
        GenerateRecipe = "Receptet az AI-tól"
        IngredientsCount = "alapanyag"
        NoRecipeYet = "Még nincs recept"
        NoRecipeHintLine1 = "Tedd tele a kamrát, és nyomd meg a gombot fent."
        NoRecipeHintLine2 = "A recept itt fog megjelenni."
        Thinking = "A séf gondolkodik…"
        RetryBtn = "Próbáljuk újra"
        SomethingWrong = "Valami félrement"

        RecipeLabel = "RECEPT"
        Minutes = "perc"
        AlternativesTitle = "Válassz változatot"
        Alt1 = "Gyors"
        Alt2 = "Laktató"
        Alt3 = "Kreatív"
        PrevVariantAria = "Előző változat"
        NextVariantAria = "Következő változat"

        ToastTitle = "Nyelv megváltozott"
        ToastBody = "A meglévő alapanyagok és a jelenleg betöltött recept az eredeti nyelvükön maradnak. Töltsd újra az oldalt, ha mindent magyarra akarsz váltani."
        ReloadLabel = "Újratöltés"
        DismissLabel = "Bezár"

        ExpiredDaysAgo = "Lejárt %d napja"
        ExpiresToday = "Ma jár le"
        ExpiresInDays = "%d nap múlva lejár"
        FreshUntil = "Friss · %s"

        FooterPrefix = "SmartPantry · kamra, csiszolva · készítette ♥-ből"
        FooterAuthor = "JustDawee"
    }

    /// Lookup table for the current language.
    let table (lang: Lang) : T =
        match lang with
        | En -> en
        | Hu -> hu

    /// Two-letter code shown on the lang toggle button.
    let code (lang: Lang) : string =
        match lang with
        | En -> "EN"
        | Hu -> "HU"

    /// Cycle to the next language.
    let next (lang: Lang) : Lang =
        match lang with
        | En -> Hu
        | Hu -> En
