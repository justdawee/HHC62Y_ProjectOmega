namespace SmartPantry

open System
open WebSharper
open WebSharper.JavaScript
open WebSharper.UI
open WebSharper.UI.Client
open WebSharper.UI.Templating
open WebSharper.UI.Notation

[<JavaScript>]
module Templates =

    type MainTemplate = Templating.Template<"Main.html", ClientLoad.FromDocument, ServerLoad.WhenChanged>

[<JavaScript>]
module Client =

    /// Recipe panel state machine.
    type RecipeState =
        | NotRequested
        | Loading
        | Loaded of RecipeBundle * int  // bundle + currently selected variant index
        | Failed of string

    // ------------------------------------------------------------------
    // Number / date helpers — WebSharper has limited .NET API in JS
    // ------------------------------------------------------------------

    let private prettyNumber (n: float) : string =
        let s = string n
        if s.EndsWith(".0") then s.Substring(0, s.Length - 2) else s

    let private formatQty (item: PantryItem) : string =
        prettyNumber item.Quantity + " " + item.Unit

    let private padInt2 (n: int) : string = if n < 10 then "0" + string n else string n

    let private toIsoDate (d: DateTime) : string =
        string d.Year + "-" + padInt2 d.Month + "-" + padInt2 d.Day

    let private parseExpiry (raw: string) : DateTime option =
        if String.IsNullOrWhiteSpace(raw) then None
        else
            let mutable result = DateTime.MinValue
            if DateTime.TryParse(raw, &result) then Some result.Date else None

    let private daysUntil (d: DateTime) : int =
        let today = DateTime.Now.Date
        int (d.Date - today).TotalDays

    // ------------------------------------------------------------------
    // Ingredient icon: tiny lookup of common foods, fallback to a generic.
    // Keys are normalized: lowercase + simplified accents.
    // ------------------------------------------------------------------

    let private normalize (s: string) : string =
        if isNull s then ""
        else
            (s.ToLower())
                .Replace("á", "a").Replace("é", "e").Replace("í", "i")
                .Replace("ó", "o").Replace("ö", "o").Replace("ő", "o")
                .Replace("ú", "u").Replace("ü", "u").Replace("ű", "u")
                .Trim()

    let private iconFor (name: string) : string =
        let key = normalize name
        let contains (subs: string list) = subs |> List.exists key.Contains
        if   contains ["egg"; "tojas"]                          then "🥚"
        elif contains ["milk"; "tej"]                            then "🥛"
        elif contains ["cheese"; "sajt"; "parmesan"; "parmezan"] then "🧀"
        elif contains ["butter"; "vaj"]                          then "🧈"
        elif contains ["yogurt"; "kefir"; "joghurt"]             then "🥛"
        elif contains ["flour"; "liszt"]                         then "🌾"
        elif contains ["rice"; "rizs"; "arborio"]                then "🍚"
        elif contains ["pasta"; "spaghetti"; "teszta"]           then "🍝"
        elif contains ["bread"; "kenyer"]                        then "🍞"
        elif contains ["sugar"; "cukor"]                         then "🍬"
        elif contains ["salt"; "so"; "pepper"; "bors"]           then "🧂"
        elif contains ["oil"; "olaj"; "olive"; "oliva"]          then "🫒"
        elif contains ["onion"; "hagyma"]                        then "🧅"
        elif contains ["garlic"; "fokhagyma"]                    then "🧄"
        elif contains ["tomato"; "paradicsom"]                   then "🍅"
        elif contains ["potato"; "krumpli"; "burgonya"]          then "🥔"
        elif contains ["carrot"; "sargarepa"; "repa"]            then "🥕"
        elif contains ["mushroom"; "gomba"; "cremini"]           then "🍄"
        elif contains ["pepper"; "paprika"]                      then "🌶️"
        elif contains ["lettuce"; "salata"]                      then "🥬"
        elif contains ["spinach"; "spenot"]                      then "🥬"
        elif contains ["broccoli"; "brokkoli"]                   then "🥦"
        elif contains ["cucumber"; "uborka"]                     then "🥒"
        elif contains ["corn"; "kukorica"]                       then "🌽"
        elif contains ["chicken"; "csirke"]                      then "🍗"
        elif contains ["beef"; "marha"]                          then "🥩"
        elif contains ["pork"; "sertes"]                         then "🥓"
        elif contains ["fish"; "hal"; "salmon"; "lazac"]         then "🐟"
        elif contains ["shrimp"; "rak"]                          then "🦐"
        elif contains ["lemon"; "citrom"]                        then "🍋"
        elif contains ["apple"; "alma"]                          then "🍎"
        elif contains ["banana"; "banan"]                        then "🍌"
        elif contains ["orange"; "narancs"]                      then "🍊"
        elif contains ["strawberry"; "eper"]                     then "🍓"
        elif contains ["honey"; "mez"]                           then "🍯"
        elif contains ["chocolate"; "csoki"]                     then "🍫"
        elif contains ["bean"; "bab"]                            then "🫘"
        elif contains ["herb"; "basil"; "bazsalikom"; "petrezselyem"; "parsley"] then "🌿"
        else "🥗"

    // ------------------------------------------------------------------
    // Pollinations.ai image URL builder
    // ------------------------------------------------------------------

    let private encodeURI (s: string) : string =
        JS.Inline<string -> string>("encodeURIComponent")(s)

    /// Build a Pollinations.ai image URL. If `token` is non-empty it is
    /// appended to lift rate limits; otherwise we use the free public tier.
    let private pollinationsUrl (token: string) (recipe: Recipe) : string =
        let prompt =
            recipe.ImagePromptHint
            + ", food photography, plated dish, soft natural light, top-down 45deg, vibrant colors, michelin presentation"
        // Seed = stable hash of title so the image stays consistent for that recipe
        let seed =
            let chars = recipe.Title.ToCharArray()
            let mutable h = 0
            for i in 0 .. chars.Length - 1 do
                h <- (h * 31 + int chars.[i]) &&& 0x7fffffff
            h % 100000
        let baseUrl =
            sprintf "https://image.pollinations.ai/prompt/%s?width=800&height=450&seed=%d&nologo=true&model=flux"
                    (encodeURI prompt) seed
        if String.IsNullOrEmpty token then baseUrl
        else baseUrl + "&token=" + encodeURI token

    // ------------------------------------------------------------------
    // Persistence: dark mode + language
    // ------------------------------------------------------------------

    let private themeKey = "sp-theme"
    let private langKey  = "sp-lang"

    let private applyDarkClass (isDark: bool) =
        let html = JS.Document.DocumentElement
        if isDark then html.ClassList.Add("dark") else html.ClassList.Remove("dark")

    let private applyLangAttr (lang: Lang) =
        let code = match lang with En -> "en" | Hu -> "hu"
        JS.Document.DocumentElement.SetAttribute("lang", code)

    let private readInitialDark () : bool =
        try
            match JS.Window.LocalStorage.GetItem(themeKey) with
            | "light" -> false
            | "dark"  -> true
            | _ -> true
        with _ -> true

    let private readInitialLang () : Lang =
        try
            match JS.Window.LocalStorage.GetItem(langKey) with
            | "hu" -> Hu
            | _    -> En
        with _ -> En

    let private langToCode (l: Lang) =
        match l with En -> "en" | Hu -> "hu"

    // ------------------------------------------------------------------
    // Expiry badge
    // ------------------------------------------------------------------

    let private renderExpiryBadge (lang: Lang) (item: PantryItem) : Doc =
        let s = Strings.table lang
        match item.ExpiryDate with
        | None -> Doc.Empty
        | Some d ->
            let days = daysUntil d
            if days < 0 then
                let text = s.ExpiredDaysAgo.Replace("%d", string (abs days))
                Templates.MainTemplate.BadgeExpired().BadgeText(text).Doc()
            elif days = 0 then
                Templates.MainTemplate.BadgeSoon().BadgeText(s.ExpiresToday).Doc()
            elif days <= 3 then
                let text = s.ExpiresInDays.Replace("%d", string days)
                Templates.MainTemplate.BadgeSoon().BadgeText(text).Doc()
            else
                let text = s.FreshUntil.Replace("%s", toIsoDate d)
                Templates.MainTemplate.BadgeFresh().BadgeText(text).Doc()

    // ------------------------------------------------------------------
    // Item card
    // ------------------------------------------------------------------

    let private renderItemCard
        (langView: View<Lang>)
        (items: ListModel<int, PantryItem>)
        (item: PantryItem)
        : Doc =
        let badgeDoc =
            langView
            |> View.Map (fun lang -> renderExpiryBadge lang item)
            |> Doc.EmbedView
        let deleteAria =
            langView
            |> View.Map (fun _ -> "Delete " + item.Name)
        Templates.MainTemplate.ItemCard()
            .Icon(iconFor item.Name)
            .Name(item.Name)
            .QtyDisplay(formatQty item)
            .ExpiryBadge([ badgeDoc ])
            .DeleteAria(deleteAria)
            .OnDelete(fun _ ->
                async {
                    let! ok = Server.DeleteItem item.Id
                    if ok then items.RemoveByKey item.Id
                } |> Async.StartImmediate)
            .Doc()

    // ------------------------------------------------------------------
    // Recipe area: NotRequested / Loading / Loaded / Failed
    // ------------------------------------------------------------------

    let private variantLabel (lang: Lang) (idx: int) : string =
        let s = Strings.table lang
        match idx with
        | 0 -> s.Alt1
        | 1 -> s.Alt2
        | _ -> s.Alt3

    let private renderRecipe
        (lang: Lang)
        (token: string)
        (state: Var<RecipeState>)
        (bundle: RecipeBundle)
        (selectedIdx: int)
        : Doc =
        let s = Strings.table lang
        let recipes = bundle.Recipes
        let count = List.length recipes
        let safeIdx =
            if selectedIdx < 0 then 0
            elif selectedIdx >= count then count - 1
            else selectedIdx
        let recipe = List.item safeIdx recipes

        // Variant tabs (only if >1 recipe)
        let tabs =
            if count <= 1 then [ Doc.Empty ]
            else
                recipes
                |> List.mapi (fun i _ ->
                    let isActive = i = safeIdx
                    let attrs =
                        if isActive
                        then Attr.Class "tag bg-fuchsia-500/30 text-fuchsia-700 dark:text-fuchsia-200 border-fuchsia-400/40"
                        else Attr.Class "text-slate-500 dark:text-slate-400 hover:bg-white/10 border-white/10"
                    Templates.MainTemplate.VariantTab()
                        .TabLabel(variantLabel lang i)
                        .TabAttrs(attrs)
                        .OnPickVariant(fun _ ->
                            match state.Value with
                            | Loaded (b, _) -> state.Value <- Loaded (b, i)
                            | _ -> ())
                        .Doc())

        let stepDocs =
            recipe.Steps
            |> List.mapi (fun i st ->
                Templates.MainTemplate.RecipeStepItem()
                    .StepN(string st.StepNumber)
                    .Instruction(st.Instruction)
                    .Delay(sprintf "%dms" (i * 50))
                    .Doc())

        let tagDocs =
            recipe.Tags
            |> List.map (fun t ->
                Templates.MainTemplate.RecipeTagBadge()
                    .TagText(t)
                    .Doc())

        let prepTimeText = sprintf "%d %s" recipe.PrepTimeMinutes s.Minutes
        let imgUrl = pollinationsUrl token recipe

        // Procedural fallback decoration — used both as a placeholder while the
        // AI image loads and as the final state if Pollinations.ai rejects us.
        // We pick a hue-stable seed off the recipe title so each variant has a
        // recognisable colour signature.
        let titleSeed =
            let chars = recipe.Title.ToCharArray()
            let mutable h = 0
            for i in 0 .. chars.Length - 1 do
                h <- (h * 31 + int chars.[i]) &&& 0x7fffffff
            h
        let hueA = titleSeed % 360
        let hueB = (titleSeed + 80) % 360
        let fallbackStyle =
            sprintf "background: linear-gradient(135deg, oklch(72%% 0.18 %d), oklch(68%% 0.16 %d));"
                    hueA hueB
        let foodEmoji = iconFor recipe.Title

        // Imperative <img> so we can hook real load/error events. Inline
        // onload/onerror attributes get stripped by WebSharper templating.
        let imgEl =
            Doc.Element "img" [
                Attr.Create "src" imgUrl
                Attr.Create "alt" recipe.Title
                Attr.Create "loading" "lazy"
                Attr.Create "class" "absolute inset-0 z-20 w-full h-full object-cover opacity-0 transition-opacity duration-500"
                on.load (fun el _ -> el.SetAttribute("style", "opacity:1"))
                on.error (fun el _ -> el.SetAttribute("style", "display:none"))
            ] []

        // Fallback layer (always rendered behind the image; revealed if image fails)
        let fallbackEl =
            Doc.Element "div" [
                Attr.Create "class" "absolute inset-0 z-10 grid place-items-center text-7xl"
                Attr.Create "style" fallbackStyle
                Attr.Create "aria-hidden" "true"
            ] [
                Doc.Element "span" [ Attr.Create "class" "drop-shadow-lg" ] [
                    Doc.TextNode foodEmoji
                ]
            ]

        Templates.MainTemplate.RecipeShow()
            .RecipeBadge(s.RecipeLabel)
            .RecipeTitle(recipe.Title)
            .PrepTime(prepTimeText)
            .RecipeImage([ (fallbackEl :> Doc); (imgEl :> Doc) ])
            .VariantTabs(tabs)
            .Tags(tagDocs)
            .Steps(stepDocs)
            .Doc()

    let Main () : Doc =

        // ---- pantry items
        let items =
            ListModel.Create
                (fun (i: PantryItem) -> i.Id)
                ([] : PantryItem list)

        // ---- form state
        let newName   = Var.Create ""
        let newQty    = Var.Create 1.0
        let newUnit   = Var.Create "pcs"
        let formError = Var.Create (None: string option)

        // ---- recipe state
        let recipeState = Var.Create NotRequested

        // ---- language
        let lang = Var.Create (readInitialLang ())
        applyLangAttr lang.Value
        lang.View |> View.Sink (fun l ->
            applyLangAttr l
            try JS.Window.LocalStorage.SetItem(langKey, langToCode l) with _ -> ())

        // ---- dark mode
        let isDark = Var.Create (readInitialDark ())
        applyDarkClass isDark.Value
        isDark.View |> View.Sink (fun v ->
            applyDarkClass v
            try JS.Window.LocalStorage.SetItem(themeKey, if v then "dark" else "light") with _ -> ())

        // ---- initial pantry load + image-API token fetch
        let imageToken = Var.Create ""
        async {
            let! initial = Server.GetItems ()
            items.Set initial
            let! tok = Server.GetImageToken ()
            imageToken.Value <- tok
        } |> Async.StartImmediate

        // ---- derived views
        let itemCountView = items.View |> View.Map Seq.length
        let itemCountStr  = itemCountView |> View.Map string
        let itemCountPad  =
            itemCountView |> View.Map (fun n -> if n < 10 then "0" + string n else string n)

        let cookNow () =
            recipeState.Value <- Loading
            let l = lang.Value
            async {
                let! r = Server.GenerateRecipes l
                recipeState.Value <-
                    match r with
                    | Ok bundle -> Loaded (bundle, 0)
                    | Error e   -> Failed e
            } |> Async.StartImmediate

        // ---- form submit
        let submitNew () =
            let s = Strings.table lang.Value
            let n = newName.Value.Trim()
            let q = newQty.Value
            let u =
                if String.IsNullOrWhiteSpace newUnit.Value then "pcs"
                else newUnit.Value.Trim()
            if n = "" then
                formError.Value <- Some s.EmptyName
            elif q < 0.0 then
                formError.Value <- Some s.NegativeQty
            elif Double.IsNaN q || Double.IsInfinity q then
                formError.Value <- Some s.InvalidQty
            else
                formError.Value <- None
                let input = {
                    Name = n
                    Quantity = q
                    Unit = u
                    ExpiryDate = None
                }
                async {
                    try
                        let! created = Server.AddItem input
                        items.Add created
                        newName.Value <- ""
                        newQty.Value <- 1.0
                    with ex ->
                        formError.Value <- Some ex.Message
                } |> Async.StartImmediate

        // ---- clear all
        let clearAll () =
            let s = Strings.table lang.Value
            if JS.Window.Confirm(s.ClearAllConfirm) then
                async {
                    let! _ = Server.DeleteAll ()
                    items.Clear()
                    recipeState.Value <- NotRequested
                } |> Async.StartImmediate

        // ---- form-error inline message
        let formErrorDoc =
            formError.View
            |> View.Map (function
                | None -> Doc.Empty
                | Some msg ->
                    Templates.MainTemplate.InlineError()
                        .ErrorText(msg)
                        .Doc())
            |> Doc.EmbedView

        // ---- empty-state Doc
        let emptyDoc =
            View.Map2 (fun seq l ->
                let s = Strings.table l
                if Seq.isEmpty seq then
                    Templates.MainTemplate.EmptyPantry()
                        .EmptyTitle(s.EmptyPantryTitle)
                        .EmptyHint(s.EmptyPantryHint)
                        .Doc()
                else Doc.Empty
            ) items.View lang.View
            |> Doc.EmbedView

        // ---- items grid
        let itemsDoc =
            ListModel.View items
            |> Doc.BindSeqCachedBy items.Key (fun item -> renderItemCard lang.View items item)

        // ---- recipe area
        let recipeAreaDoc =
            View.Map3 (fun st l tok -> (st, l, tok))
                recipeState.View lang.View imageToken.View
            |> View.Map (fun (st, l, tok) ->
                let s = Strings.table l
                match st with
                | NotRequested ->
                    Templates.MainTemplate.NoRecipeYet()
                        .NoRecipeTitle(s.NoRecipeYet)
                        .NoRecipeLine1(s.NoRecipeHintLine1)
                        .NoRecipeLine2(s.NoRecipeHintLine2)
                        .Doc()
                | Loading ->
                    Templates.MainTemplate.RecipeLoading()
                        .ThinkingLabel(s.Thinking)
                        .Doc()
                | Failed msg ->
                    Templates.MainTemplate.RecipeError()
                        .ErrorTitle(s.SomethingWrong)
                        .ErrorMessage(msg)
                        .RetryLabel(s.RetryBtn)
                        .OnCook(fun _ -> cookNow ())
                        .Doc()
                | Loaded (bundle, idx) ->
                    renderRecipe l tok recipeState bundle idx)
            |> Doc.EmbedView

        // ---- string slots that depend on lang
        let strView (f: Strings.T -> string) : View<string> =
            lang.View |> View.Map (fun l -> f (Strings.table l))

        // ---- disabled-state attrs: dynamically toggle the `disabled` HTML
        // attribute. Empty string keeps the attr present, "" removed by setting
        // null. We use Attr.DynamicProp on the boolean DOM `disabled` property.
        let cookDisabledProp =
            View.Map2 (fun cnt st ->
                box (cnt = 0 || st = Loading)
            ) itemCountView recipeState.View
        let cookDisabledAttr = Attr.DynamicProp "disabled" cookDisabledProp

        let clearDisabledProp =
            itemCountView |> View.Map (fun cnt -> box (cnt = 0))
        let clearDisabledAttr = Attr.DynamicProp "disabled" clearDisabledProp

        let langCodeView = lang.View |> View.Map Strings.code

        // ---- assemble App template
        Templates.MainTemplate.App()
            .Tagline(strView (fun s -> s.Tagline))
            .IngredientsCount(itemCountStr)
            .IngredientsOnHandLabel(strView (fun s -> s.IngredientsOnHand))
            .LangCode(langCodeView)
            .LangBtnAttrs(Attr.Empty)
            .OnToggleLang(fun _ -> lang.Value <- Strings.next lang.Value)
            .OnToggleDark(fun _ -> isDark.Value <- not isDark.Value)
            .YourPantryLabel(strView (fun s -> s.YourPantry))
            .ItemCountPadded(itemCountPad)
            .ItemsLabel(strView (fun s -> s.Items))
            .ClearAllLabel(strView (fun s -> s.ClearAll))
            .ClearBtnAttrs(clearDisabledAttr)
            .OnClearAll(fun _ -> clearAll ())
            .NewName(newName)
            .NewQty(newQty)
            .NewUnit(newUnit)
            .AddPlaceholder(strView (fun s -> s.AddIngredientPlaceholder))
            .AddBtnAria(strView (fun s -> s.AddBtnLabel))
            .UnitPcs(strView  (fun s -> s.Pcs))
            .UnitG(strView    (fun s -> s.Grams))
            .UnitKg(strView   (fun s -> s.Kg))
            .UnitMl(strView   (fun s -> s.Ml))
            .UnitL(strView    (fun s -> s.Liter))
            .UnitCup(strView  (fun s -> s.Cup))
            .UnitTbsp(strView (fun s -> s.Tbsp))
            .UnitTsp(strView  (fun s -> s.Tsp))
            .OnAdd(fun e ->
                e.Event.PreventDefault()
                submitNew ())
            .Items([ itemsDoc ])
            .EmptyState([ emptyDoc ])
            .FormError([ formErrorDoc ])
            .PantryFootNote(View.Const "")
            .AiChefLabel(strView (fun s -> s.AiChef))
            .PoweredByLabel(strView (fun s -> s.PoweredBy))
            .GenerateLabel(strView (fun s -> s.GenerateRecipe))
            .IngredientsBadgeLabel(strView (fun s -> s.IngredientsCount))
            .CookBtnAttrs(cookDisabledAttr)
            .OnCook(fun _ -> cookNow ())
            .RecipeArea([ recipeAreaDoc ])
            .FooterText(strView (fun s -> s.FooterText))
            .Doc()
