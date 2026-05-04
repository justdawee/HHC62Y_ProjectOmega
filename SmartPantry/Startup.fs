open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
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

    builder.Services.AddWebSharperServices() |> ignore
    builder.Services
        .AddAuthentication("WebSharper")
        .AddCookie("WebSharper", fun _ -> ())
    |> ignore

    // Named HttpClient for OpenAI / TheMealDB calls — DI manages socket pooling.
    builder.Services.AddHttpClient("openai", fun (c: System.Net.Http.HttpClient) ->
        c.Timeout <- TimeSpan.FromSeconds(60.0))
    |> ignore

    let app = builder.Build()

    if not (app.Environment.IsDevelopment()) then
        app.UseExceptionHandler("/Error")
            .UseHsts()
        |> ignore

    // Anonymous identity cookie — must run before WebSharper RPC handlers.
    app.Use(fun ctx next -> spUidMiddleware ctx next) |> ignore

    // Static-file cache headers. The Tailwind output and WebSharper bundle
    // are not content-hashed, so a long edge cache (Cloudflare defaulted to
    // 4h) makes deploys invisible to repeat visitors. 5 minutes is a sane
    // compromise: deploys propagate quickly, origin still gets a break.
    let staticFileOpts =
        let o = StaticFileOptions()
        o.OnPrepareResponse <- fun ctx ->
            ctx.Context.Response.Headers["Cache-Control"] <-
                Microsoft.Extensions.Primitives.StringValues "public,max-age=300,must-revalidate"
        o

    app
#if DEBUG
        .UseWebSharperScriptRedirect(startVite = true)
#endif
        .UseAuthentication()
        .UseStaticFiles(staticFileOpts)
        .UseWebSharper(fun ws -> ws.Sitelet(Site.Main) |> ignore)
    |> ignore

    app.Run()

    0 // Exit code
