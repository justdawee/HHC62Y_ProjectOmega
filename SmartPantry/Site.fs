namespace SmartPantry

open WebSharper
open WebSharper.Sitelets
open WebSharper.UI
open WebSharper.UI.Server

type EndPoint =
    | [<EndPoint "/">] Home

module Site =
    open WebSharper.UI.Html

    open type WebSharper.UI.ClientServer

    let private homeBody () : Doc list = [
        client (Client.Main())
    ]

    let HomePage (ctx: Context<EndPoint>) =
        Content.Page(
            Templates.MainTemplate()
                .Body(homeBody ())
                .Doc(),
            Bundle = "home"
        )

    [<Website>]
    let Main =
        Application.MultiPage (fun ctx endpoint ->
            match endpoint with
            | EndPoint.Home -> HomePage ctx
        )
