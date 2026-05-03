namespace SmartPantry

open WebSharper
open WebSharper.UI
open WebSharper.UI.Templating
open WebSharper.UI.Notation

[<JavaScript>]
module Templates =

    type MainTemplate = Templating.Template<"Main.html", ClientLoad.FromDocument, ServerLoad.WhenChanged>

[<JavaScript>]
module Client =

    /// Phase 4 placeholder. The full reactive UI (ListModel + Vars, RPC bindings,
    /// dark mode, animations) is wired in the next milestone.
    let Main () : Doc =
        Doc.Empty
