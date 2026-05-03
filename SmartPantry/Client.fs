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
    // Number / date helpers
    // ------------------------------------------------------------------

    let private prettyNumber (n: float) : string =
        let s = string n
        if s.EndsWith(".0") then s.Substring(0, s.Length - 2) else s

    let private formatQty (item: PantryItem) : string =
        prettyNumber item.Quantity + " " + item.Unit

    let private padInt2 (n: int) : string = if n < 10 then "0" + string n else string n

    let private toIsoDate (d: DateTime) : string =
        string d.Year + "-" + padInt2 d.Month + "-" + padInt2 d.Day

    let private daysUntil (d: DateTime) : int =
        let today = DateTime.Now.Date
        int (d.Date - today).TotalDays

    // ------------------------------------------------------------------
    // Ingredient icon: short lookup, then catalog fallback, then generic.
    // ------------------------------------------------------------------

    let private iconFor (name: string) : string =
        match Catalog.findByName name with
        | Some s -> s.Icon
        | None -> "🥗"

    // ------------------------------------------------------------------
    // Pollinations.ai image URL
    // ------------------------------------------------------------------

    let private encodeURI (s: string) : string =
        JS.Inline<string -> string>("encodeURIComponent")(s)

    let private titleHash (title: string) : int =
        let chars = title.ToCharArray()
        let mutable h = 0
        for i in 0 .. chars.Length - 1 do
            h <- (h * 31 + int chars.[i]) &&& 0x7fffffff
        h

    let private pollinationsUrl (token: string) (recipe: Recipe) : string =
        let prompt =
            recipe.ImagePromptHint
            + ", food photography, plated dish, soft natural light, top-down 45deg, vibrant colors, michelin presentation"
        let seed = (titleHash recipe.Title) % 100000
        let baseUrl =
            sprintf "https://image.pollinations.ai/prompt/%s?width=800&height=450&seed=%d&nologo=true&model=flux"
                    (encodeURI prompt) seed
        if String.IsNullOrEmpty token then baseUrl
        else baseUrl + "&token=" + encodeURI token

    // ------------------------------------------------------------------
    // Persistence
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
            langView |> View.Map (fun _ -> "Delete " + item.Name)
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
    // Variant slider rendering (dot bar + arrows)
    // ------------------------------------------------------------------

    let private variantLabelText (lang: Lang) (idx: int) : string =
        let s = Strings.table lang
        match idx with
        | 0 -> s.Alt1
        | 1 -> s.Alt2
        | _ -> s.Alt3

    let private renderVariantNav
        (lang: Lang)
        (state: Var<RecipeState>)
        (count: int)
        (selectedIdx: int)
        : Doc =
        let s = Strings.table lang
        let go (newIdx: int) =
            match state.Value with
            | Loaded (b, _) ->
                let n = ((newIdx % count) + count) % count
                state.Value <- Loaded (b, n)
            | _ -> ()
        let dots =
            [ 0 .. count - 1 ]
            |> List.map (fun i ->
                let cls =
                    if i = selectedIdx
                    then Attr.Class "var-dot is-active"
                    else Attr.Class "var-dot"
                let aria = sprintf "Variant %d" (i + 1)
                Templates.MainTemplate.VariantDot()
                    .DotAttrs(cls)
                    .DotAria(aria)
                    .OnPickDot(fun _ -> go i)
                    .Doc())
        Templates.MainTemplate.VariantNavBar()
            .VariantLabel(variantLabelText lang selectedIdx)
            .Dots(dots)
            .OnPrev(fun _ -> go (selectedIdx - 1))
            .OnNext(fun _ -> go (selectedIdx + 1))
            .PrevAttrs(Attr.Empty)
            .NextAttrs(Attr.Empty)
            .Doc()

    // ------------------------------------------------------------------
    // Recipe area renderer
    // ------------------------------------------------------------------

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

        // Procedural fallback decoration — recipe-stable hue
        let titleSeed = titleHash recipe.Title
        let hueA = titleSeed % 360
        let hueB = (titleSeed + 80) % 360
        let fallbackStyle =
            sprintf "background: linear-gradient(135deg, oklch(72%% 0.18 %d), oklch(68%% 0.16 %d));"
                    hueA hueB
        let foodEmoji = iconFor recipe.Title

        let imgUrl = pollinationsUrl token recipe

        let imgEl =
            Doc.Element "img" [
                Attr.Create "src" imgUrl
                Attr.Create "alt" recipe.Title
                Attr.Create "loading" "lazy"
                Attr.Create "class" "absolute inset-0 z-20 w-full h-full object-cover opacity-0 transition-opacity duration-500"
                on.load (fun el _ -> el.SetAttribute("style", "opacity:1"))
                on.error (fun el _ -> el.SetAttribute("style", "display:none"))
            ] []

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
        let nav = renderVariantNav lang state count safeIdx

        // Wrap the entire show in a slide-in animator container so the
        // transition between variants feels smooth.
        let body =
            Templates.MainTemplate.RecipeShow()
                .RecipeBadge(s.RecipeLabel)
                .RecipeTitle(recipe.Title)
                .PrepTime(prepTimeText)
                .RecipeImage([ (fallbackEl :> Doc); (imgEl :> Doc) ])
                .VariantNav([ nav ])
                .Tags(tagDocs)
                .Steps(stepDocs)
                .Doc()
        Doc.Element "div" [
            Attr.Create "class" "recipe-slide"
            Attr.Create "data-variant" (string safeIdx)
        ] [ body ]

    // ------------------------------------------------------------------
    // Custom unit dropdown
    // ------------------------------------------------------------------

    let private unitOptionsFor (s: Strings.T) =
        [ "pcs",  s.Pcs
          "g",    s.Grams
          "kg",   s.Kg
          "ml",   s.Ml
          "l",    s.Liter
          "cup",  s.Cup
          "tbsp", s.Tbsp
          "tsp",  s.Tsp ]

    let private unitLabel (lang: Lang) (unit: string) : string =
        let s = Strings.table lang
        unitOptionsFor s
        |> List.tryFind (fun (k, _) -> k = unit)
        |> Option.map snd
        |> Option.defaultValue unit

    let private renderUnitDropdown
        (lang: Lang)
        (newUnit: Var<string>)
        (open': Var<bool>)
        : Doc =
        let s = Strings.table lang

        // Reactive chevron rotation when the dropdown opens.
        let chevronAttrs =
            Attr.DynamicClassPred "rotate-180" open'.View

        let trigger =
            Templates.MainTemplate.UnitTrigger()
                .UnitLabel(unitLabel lang newUnit.Value)
                .ChevronAttrs(chevronAttrs)
                .OnUnitToggle(fun _ -> open'.Value <- not open'.Value)
                .Doc()

        // Panel rendered conditionally
        let panelView =
            View.Map2 (fun isOpen u -> (isOpen, u)) open'.View newUnit.View
            |> View.Map (fun (isOpen, u) ->
                if not isOpen then Doc.Empty
                else
                    let opts =
                        unitOptionsFor s
                        |> List.map (fun (key, label) ->
                            let isSel = key = u
                            let cls =
                                if isSel
                                then Attr.Class "uo-row is-selected text-slate-900 dark:text-white"
                                else Attr.Class "uo-row text-slate-700 dark:text-slate-200"
                            let check =
                                if isSel
                                then
                                    Doc.Element "svg" [
                                        Attr.Create "viewBox" "0 0 24 24"
                                        Attr.Create "class" "w-3 h-3 text-fuchsia-500 dark:text-fuchsia-300"
                                        Attr.Create "fill" "none"
                                        Attr.Create "stroke" "currentColor"
                                        Attr.Create "stroke-width" "3"
                                        Attr.Create "stroke-linecap" "round"
                                        Attr.Create "stroke-linejoin" "round"
                                    ] [
                                        Doc.Element "path" [ Attr.Create "d" "M5 12l5 5L20 7" ] []
                                    ]
                                    :> Doc
                                else Doc.Empty
                            Templates.MainTemplate.UnitOption()
                                .UnitOptLabel(label)
                                .OptAttrs(cls)
                                .Check([ check ])
                                .OnUnitPick(fun _ ->
                                    newUnit.Value <- key
                                    open'.Value <- false)
                                .Doc())
                    Templates.MainTemplate.UnitPanel()
                        .UnitOptions(opts)
                        .Doc())
            |> Doc.EmbedView

        Doc.Concat [ trigger; panelView ]

    // ------------------------------------------------------------------
    // Suggestion dropdown
    // ------------------------------------------------------------------

    let private renderSuggestions
        (lang: Lang)
        (newName: Var<string>)
        (newUnit: Var<string>)
        (open': Var<bool>)
        (highlightIdx: Var<int>)
        : Doc =
        View.Map3 (fun nm isOpen hi -> (nm, isOpen, hi)) newName.View open'.View highlightIdx.View
        |> View.Map (fun (name, isOpen, hi) ->
            if not isOpen then Doc.Empty
            else
                let suggestions = Catalog.suggest lang name 6
                if List.isEmpty suggestions then Doc.Empty
                else
                    let rows =
                        suggestions
                        |> List.mapi (fun i s ->
                            let displayName =
                                match lang with En -> s.En | Hu -> s.Hu
                            let cls =
                                if i = hi
                                then Attr.Class "sg-row is-active"
                                else Attr.Class "sg-row"
                            Templates.MainTemplate.SuggestionRow()
                                .SuggestionIcon(s.Icon)
                                .SuggestionName(displayName)
                                .SuggestionUnit(unitLabel lang s.Unit)
                                .RowAttrs(cls)
                                .OnPickSuggestion(fun e ->
                                    // mousedown fires before blur; preventDefault
                                    // keeps the input focused so the form-submit
                                    // logic still works.
                                    e.Event.PreventDefault()
                                    newName.Value <- displayName
                                    newUnit.Value <- s.Unit
                                    open'.Value <- false
                                    highlightIdx.Value <- 0)
                                .Doc())
                    Templates.MainTemplate.SuggestionPanel()
                        .SuggestionRows(rows)
                        .Doc())
        |> Doc.EmbedView

    // ------------------------------------------------------------------
    // Main entry
    // ------------------------------------------------------------------

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

        // ---- suggestion + unit-dropdown UI state
        let suggOpen = Var.Create false
        let suggHi   = Var.Create 0
        let unitOpen = Var.Create false

        // ---- recipe state
        let recipeState = Var.Create NotRequested

        // ---- language + dark mode
        let lang = Var.Create (readInitialLang ())
        applyLangAttr lang.Value
        lang.View |> View.Sink (fun l ->
            applyLangAttr l
            try JS.Window.LocalStorage.SetItem(langKey, langToCode l) with _ -> ())

        let isDark = Var.Create (readInitialDark ())
        applyDarkClass isDark.Value
        isDark.View |> View.Sink (fun v ->
            applyDarkClass v
            try JS.Window.LocalStorage.SetItem(themeKey, if v then "dark" else "light") with _ -> ())

        // ---- lang switch toast
        let toastVisible = Var.Create false

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

        // ---- handlers
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
                        newUnit.Value <- "pcs"
                        suggOpen.Value <- false
                    with ex ->
                        formError.Value <- Some ex.Message
                } |> Async.StartImmediate

        let clearAll () =
            let s = Strings.table lang.Value
            if JS.Window.Confirm(s.ClearAllConfirm) then
                async {
                    let! _ = Server.DeleteAll ()
                    items.Clear()
                    recipeState.Value <- NotRequested
                } |> Async.StartImmediate

        let onLangToggle () =
            let pantryNotEmpty = not (Seq.isEmpty items.Value)
            let hasRecipe =
                match recipeState.Value with
                | Loaded _ | Failed _ -> true
                | _ -> false
            lang.Value <- Strings.next lang.Value
            // If there's existing localized content, gently prompt the user.
            if pantryNotEmpty || hasRecipe then
                toastVisible.Value <- true

        // ---- close dropdowns when clicking outside the form
        let onGlobalMouseDown (e: Dom.Event) =
            let target = e.Target :?> Dom.Element
            // Close unit dropdown if click is outside any add-row element
            let inAddRow =
                let mutable el : Dom.Element = target
                let mutable found = false
                let mutable safety = 0
                while not (isNull el) && not found && safety < 30 do
                    let cls = el.GetAttribute("class")
                    if not (isNull cls) && cls.Contains("add-row") then found <- true
                    else el <- el.ParentNode :?> Dom.Element
                    safety <- safety + 1
                found
            if not inAddRow then
                if unitOpen.Value then unitOpen.Value <- false
                if suggOpen.Value then suggOpen.Value <- false

        JS.Document.AddEventListener("mousedown", onGlobalMouseDown, false)

        // ---- name input handlers
        let onNameFocus () =
            // Re-open suggestions if the name field has any text
            if newName.Value.Trim().Length > 0 then
                suggOpen.Value <- true

        let onNameBlur () =
            // Defer close so a click on a suggestion still fires its mousedown
            JS.Window.SetTimeout((fun () ->
                suggOpen.Value <- false), 150) |> ignore

        let onNameKey (e: Dom.KeyboardEvent) =
            let count =
                let l = lang.Value
                Catalog.suggest l (newName.Value) 6 |> List.length
            if count = 0 then
                if e.Key = "Enter" then suggOpen.Value <- false
            else
                match e.Key with
                | "ArrowDown" ->
                    e.PreventDefault()
                    suggOpen.Value <- true
                    suggHi.Value <- (suggHi.Value + 1 + count) % count
                | "ArrowUp" ->
                    e.PreventDefault()
                    suggOpen.Value <- true
                    suggHi.Value <- (suggHi.Value - 1 + count) % count
                | "Escape" ->
                    suggOpen.Value <- false
                | "Enter" ->
                    if suggOpen.Value then
                        e.PreventDefault()
                        let l = lang.Value
                        let suggestions = Catalog.suggest l (newName.Value) 6
                        let idx =
                            if suggHi.Value < 0 then 0
                            elif suggHi.Value >= List.length suggestions then 0
                            else suggHi.Value
                        if not (List.isEmpty suggestions) then
                            let picked = List.item idx suggestions
                            let display =
                                match l with En -> picked.En | Hu -> picked.Hu
                            newName.Value <- display
                            newUnit.Value <- picked.Unit
                            suggOpen.Value <- false
                            suggHi.Value <- 0
                | _ -> ()

        // ---- when user types in the name field, open suggestions and reset hi
        newName.View |> View.Sink (fun v ->
            let l = lang.Value
            let any = (Catalog.suggest l v 6) |> List.isEmpty |> not
            if v.Trim() = "" then
                suggOpen.Value <- false
            else
                suggOpen.Value <- any
            suggHi.Value <- 0
            // Auto-fill unit if user typed an exact match
            match Catalog.findByName v with
            | Some s -> newUnit.Value <- s.Unit
            | None -> ())

        // ---- Lang change resets suggestion state
        lang.View |> View.Sink (fun _ ->
            suggHi.Value <- 0
            suggOpen.Value <- false
            unitOpen.Value <- false)

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

        // ---- string slots
        let strView (f: Strings.T -> string) : View<string> =
            lang.View |> View.Map (fun l -> f (Strings.table l))

        // ---- disabled-state attrs
        let cookDisabledProp =
            View.Map2 (fun cnt st ->
                box (cnt = 0 || st = Loading)
            ) itemCountView recipeState.View
        let cookDisabledAttr = Attr.DynamicProp "disabled" cookDisabledProp

        let clearDisabledProp =
            itemCountView |> View.Map (fun cnt -> box (cnt = 0))
        let clearDisabledAttr = Attr.DynamicProp "disabled" clearDisabledProp

        let langCodeView = lang.View |> View.Map Strings.code

        // ---- unit dropdown Doc — re-binds with current lang/unit value
        let unitDropdownDoc =
            View.Map2 (fun l u -> (l, u)) lang.View newUnit.View
            |> View.Map (fun (l, _) -> renderUnitDropdown l newUnit unitOpen)
            |> Doc.EmbedView

        // ---- suggestions Doc — re-binds when language changes so localized
        // names + unit labels stay in sync.
        let suggestionsDoc =
            lang.View
            |> View.Map (fun l -> renderSuggestions l newName newUnit suggOpen suggHi)
            |> Doc.EmbedView

        // ---- toast Doc
        let toastDoc =
            View.Map2 (fun visible l -> (visible, l)) toastVisible.View lang.View
            |> View.Map (fun (visible, l) ->
                if not visible then Doc.Empty
                else
                    let s = Strings.table l
                    Templates.MainTemplate.LangToast()
                        .ToastTitle(s.ToastTitle)
                        .ToastBody(s.ToastBody)
                        .ReloadLabel(s.ReloadLabel)
                        .DismissLabel(s.DismissLabel)
                        .OnReload(fun _ -> JS.Window.Location.Reload())
                        .OnDismissToast(fun _ -> toastVisible.Value <- false)
                        .Doc())
            |> Doc.EmbedView

        // ---- assemble App template
        let appDoc =
            Templates.MainTemplate.App()
                .Tagline(strView (fun s -> s.Tagline))
                .IngredientsCount(itemCountStr)
                .IngredientsOnHandLabel(strView (fun s -> s.IngredientsOnHand))
                .LangCode(langCodeView)
                .LangBtnAttrs(Attr.Empty)
                .OnToggleLang(fun _ -> onLangToggle ())
                .OnToggleDark(fun _ -> isDark.Value <- not isDark.Value)
                .YourPantryLabel(strView (fun s -> s.YourPantry))
                .ItemCountPadded(itemCountPad)
                .ItemsLabel(strView (fun s -> s.Items))
                .ClearAllLabel(strView (fun s -> s.ClearAll))
                .ClearBtnAttrs(clearDisabledAttr)
                .OnClearAll(fun _ -> clearAll ())
                .NewName(newName)
                .NewQty(newQty)
                .OnNameFocus(fun _ -> onNameFocus ())
                .OnNameBlur(fun _ -> onNameBlur ())
                .OnNameKey(fun e -> onNameKey e.Event)
                .AddPlaceholder(strView (fun s -> s.AddIngredientPlaceholder))
                .AddBtnAria(strView (fun s -> s.AddBtnLabel))
                .UnitDropdown([ unitDropdownDoc ])
                .Suggestions([ suggestionsDoc ])
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

        Doc.Concat [ appDoc; toastDoc ]
