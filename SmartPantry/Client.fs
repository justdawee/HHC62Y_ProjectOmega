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

    /// State of the recipe modal — drives loading skeleton vs loaded body vs hidden.
    type RecipeState =
        | Hidden
        | Loading
        | Loaded of Recipe
        | Failed of string

    /// Trim trailing ".0" so "1" prints as "1", not "1.0"; keep "1.5" intact.
    let private prettyNumber (n: float) : string =
        let s = string n
        if s.EndsWith(".0") then s.Substring(0, s.Length - 2) else s

    let private formatQty (item: PantryItem) : string =
        prettyNumber item.Quantity + " " + item.Unit

    let private parseExpiry (raw: string) : DateTime option =
        if String.IsNullOrWhiteSpace(raw) then None
        else
            let mutable result = DateTime.MinValue
            if DateTime.TryParse(raw, &result)
            then Some result.Date
            else None

    let private pad2 (n: int) : string =
        if n < 10 then "0" + string n else string n

    let private toIsoDate (d: DateTime) : string =
        string d.Year + "-" + pad2 d.Month + "-" + pad2 d.Day

    let private daysUntil (d: DateTime) : int =
        let today = DateTime.Now.Date
        int (d.Date - today).TotalDays

    let private expiryBadge (item: PantryItem) : (string * string) option =
        match item.ExpiryDate with
        | None -> None
        | Some d ->
            let days = daysUntil d
            if days < 0 then
                Some ("expired", sprintf "Lejárt %d napja" (abs days))
            elif days = 0 then
                Some ("soon", "Ma jár le")
            elif days <= 3 then
                Some ("soon", sprintf "%d nap múlva lejár" days)
            else
                Some ("fresh", sprintf "Friss · %s" (toIsoDate d))

    // ---- Dark mode persistence

    let private storageKey = "sp-theme"

    let private applyDarkClass (isDark: bool) =
        let html = JS.Document.DocumentElement
        if isDark then html.ClassList.Add("dark")
        else html.ClassList.Remove("dark")

    let private readInitialDark () : bool =
        try
            match JS.Window.LocalStorage.GetItem(storageKey) with
            | "light" -> false
            | "dark" -> true
            | _ -> true   // designer default
        with _ -> true

    // ---- Render helpers

    let private renderExpiryBadge (item: PantryItem) : Doc =
        match expiryBadge item with
        | None -> Doc.Empty
        | Some ("expired", text) ->
            Templates.MainTemplate.BadgeExpired().BadgeText(text).Doc()
        | Some ("soon", text) ->
            Templates.MainTemplate.BadgeSoon().BadgeText(text).Doc()
        | Some (_, text) ->
            Templates.MainTemplate.BadgeFresh().BadgeText(text).Doc()

    let private renderItemCard
        (items: ListModel<int, PantryItem>)
        (item: PantryItem)
        : Doc =
        Templates.MainTemplate.ItemCard()
            .Name(item.Name)
            .QtyDisplay(formatQty item)
            .ExpiryBadge([ renderExpiryBadge item ])
            .OnDelete(fun _ ->
                async {
                    let! ok = Server.DeleteItem item.Id
                    if ok then items.RemoveByKey item.Id
                } |> Async.StartImmediate)
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
        let newUnit   = Var.Create "db"
        let newExpiry = Var.Create ""
        let formError = Var.Create (None: string option)

        // ---- recipe modal state
        let recipeState = Var.Create Hidden

        // ---- dark mode
        let isDark = Var.Create (readInitialDark ())
        applyDarkClass isDark.Value
        isDark.View
        |> View.Sink (fun v ->
            applyDarkClass v
            try JS.Window.LocalStorage.SetItem(storageKey, if v then "dark" else "light") with _ -> ())

        // ---- initial load
        async {
            let! initial = Server.GetItems ()
            items.Set initial
        } |> Async.StartImmediate

        // ---- derived views
        let itemCount =
            items.View
            |> View.Map (fun seq -> seq |> Seq.length |> string)

        // ---- recipe trigger
        let cookNow () =
            recipeState.Value <- Loading
            async {
                let! r = Server.GenerateRecipe ()
                recipeState.Value <-
                    match r with
                    | Ok recipe -> Loaded recipe
                    | Error e -> Failed e
            } |> Async.StartImmediate

        let hideModal () =
            recipeState.Value <- Hidden

        // ---- modal body — re-rendered per state change via View.Map, but the
        // outer modal shell (with its OnCloseModal handler) is built once and
        // wraps the body in a `ws-replace="ModalBody"` slot.
        let modalBodyDoc =
            recipeState.View
            |> View.Map (fun state ->
                match state with
                | Hidden -> Doc.Empty
                | Loading ->
                    Templates.MainTemplate.RecipeLoading().Doc()
                | Failed msg ->
                    // The retry button needs a handler; one-shot instantiation
                    // is fine because each Failed transition produces a fresh
                    // template that's mounted only briefly.
                    Templates.MainTemplate.RecipeError()
                        .ErrorMessage(msg)
                        .OnCook(fun _ -> cookNow ())
                        .Doc()
                | Loaded recipe ->
                    let stepDocs =
                        recipe.Steps
                        |> List.mapi (fun i s ->
                            Templates.MainTemplate.RecipeStepItem()
                                .StepN(string s.StepNumber)
                                .Instruction(s.Instruction)
                                .Delay(sprintf "%dms" (i * 60))
                                .Doc())
                    let tagDocs =
                        recipe.Tags
                        |> List.map (fun t ->
                            Templates.MainTemplate.RecipeTagBadge()
                                .TagText(t)
                                .Doc())
                    Templates.MainTemplate.RecipeLoaded()
                        .RecipeTitle(recipe.Title)
                        .PrepTime(sprintf "%d perc" recipe.PrepTimeMinutes)
                        .Steps(stepDocs)
                        .Tags(tagDocs)
                        .Doc())
            |> Doc.EmbedView

        // Stable modal shell instantiated once, with reactive body slot.
        let modalShell =
            Templates.MainTemplate.RecipeModal()
                .ModalBody([ modalBodyDoc ])
                .OnCloseModal(fun _ -> hideModal ())
                .Doc()

        let modalDoc =
            recipeState.View
            |> View.Map (fun state ->
                match state with
                | Hidden -> Doc.Empty
                | _ -> modalShell)
            |> Doc.EmbedView

        // Esc key closes modal
        JS.Document.AddEventListener("keydown", (fun (e: Dom.Event) ->
            let ke = e :?> Dom.KeyboardEvent
            if ke.Key = "Escape" && recipeState.Value <> Hidden then
                hideModal ()), false)

        // ---- sticky CTA — instantiated ONCE outside View.Map, then conditionally
        // mounted. Re-instantiating the template each tick would drop the handler.
        let cookCta =
            Templates.MainTemplate.CookCta()
                .CookLabel("Mit főzzek?")
                .OnCook(fun _ -> cookNow ())
                .Doc()
        let stickyDoc =
            items.View
            |> View.Map (fun seq -> if Seq.isEmpty seq then Doc.Empty else cookCta)
            |> Doc.EmbedView

        // ---- empty-state — built once, embedded reactively.
        let emptyPantryDoc = Templates.MainTemplate.EmptyPantry().Doc()
        let emptyDoc =
            items.View
            |> View.Map (fun seq ->
                if Seq.isEmpty seq then emptyPantryDoc else Doc.Empty)
            |> Doc.EmbedView

        // ---- items grid Doc
        let itemsDoc =
            ListModel.View items
            |> Doc.BindSeqCachedBy items.Key (fun item -> renderItemCard items item)

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

        // ---- form submit handler
        let submitNew () =
            let n = newName.Value.Trim()
            let q = newQty.Value
            let u =
                if String.IsNullOrWhiteSpace newUnit.Value then "db"
                else newUnit.Value.Trim()
            if n = "" then
                formError.Value <- Some "Adj meg egy nevet."
            elif q < 0.0 then
                formError.Value <- Some "A mennyiség nem lehet negatív."
            elif Double.IsNaN q || Double.IsInfinity q then
                formError.Value <- Some "Érvénytelen mennyiség."
            else
                formError.Value <- None
                let input = {
                    Name = n
                    Quantity = q
                    Unit = u
                    ExpiryDate = parseExpiry newExpiry.Value
                }
                async {
                    try
                        let! created = Server.AddItem input
                        items.Add created
                        newName.Value <- ""
                        newQty.Value <- 1.0
                        newExpiry.Value <- ""
                    with ex ->
                        formError.Value <- Some ex.Message
                } |> Async.StartImmediate

        // ---- assemble the App template
        Templates.MainTemplate.App()
            .ItemCount(itemCount)
            .NewName(newName)
            .NewQty(newQty)
            .NewUnit(newUnit)
            .NewExpiry(newExpiry)
            .OnAdd(fun e ->
                e.Event.PreventDefault()
                submitNew ())
            .Items([ itemsDoc ])
            .EmptyState([ emptyDoc ])
            .FormError([ formErrorDoc ])
            .StickyCta([ stickyDoc ])
            .Modal([ modalDoc ])
            .OnToggleDark(fun _ -> isDark.Value <- not isDark.Value)
            .Doc()
