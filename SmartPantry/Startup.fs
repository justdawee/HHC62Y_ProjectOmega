open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open WebSharper.AspNetCore
open SmartPantry

/// Anonymous user identity middleware.
/// Each visitor gets an `sp_uid` cookie containing a random GUID on first request.
/// Subsequent requests reuse it. The value is also stashed in HttpContext.Items
/// so RPC handlers can read it via Web.Remoting.GetContext().
let private spUidMiddleware (ctx: HttpContext) (next: RequestDelegate) =
    task {
        let mutable uid =
            match ctx.Request.Cookies.TryGetValue(UserContext.cookieName) with
            | true, v when not (String.IsNullOrWhiteSpace v) -> v
            | _ -> ""

        if String.IsNullOrEmpty uid then
            uid <- Guid.NewGuid().ToString("N")
            let opts = CookieOptions()
            opts.HttpOnly <- true
            opts.SameSite <- SameSiteMode.Lax
            opts.Secure <- ctx.Request.IsHttps
            opts.Expires <- DateTimeOffset.UtcNow.AddYears(1)
            opts.Path <- "/"
            ctx.Response.Cookies.Append(UserContext.cookieName, uid, opts)

        do! next.Invoke(ctx)
    } :> System.Threading.Tasks.Task

[<EntryPoint>]
let main args =
    // Initialize SQLite database (creates file, schema, indexes if missing).
    Database.init ()

    let builder = WebApplication.CreateBuilder(args)

    builder.Services
        .AddWebSharper()
        .AddAuthentication("WebSharper")
        .AddCookie("WebSharper", fun _ -> ())
    |> ignore

    // Named HttpClient for Groq API — DI manages socket pooling.
    builder.Services.AddHttpClient("groq", fun (c: System.Net.Http.HttpClient) ->
        c.Timeout <- TimeSpan.FromSeconds(35.0))
    |> ignore

    let app = builder.Build()

    if not (app.Environment.IsDevelopment()) then
        app.UseExceptionHandler("/Error")
            .UseHsts()
        |> ignore

    // Anonymous identity cookie — must run before WebSharper RPC handlers.
    app.Use(fun ctx next -> spUidMiddleware ctx next) |> ignore

    app
#if DEBUG
        .UseWebSharperScriptRedirect(startVite = true)
#endif
        .UseAuthentication()
        .UseStaticFiles()
        .UseWebSharper(fun ws -> ws.Sitelet(Site.Main) |> ignore)
    |> ignore

    app.Run()

    0 // Exit code
