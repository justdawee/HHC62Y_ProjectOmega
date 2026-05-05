open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Hosting
open WebSharper.AspNetCore
open SmartPantry

/// Validates that a presented `sp_uid` cookie value matches the format we
/// would have minted ourselves: a 32-character hexadecimal GUID with no
/// dashes. Rejecting anything else stops attackers from supplying multi-MB
/// cookie values to bloat SQL parameters or share/squat user IDs.
let private isValidUid (uid: string) =
    if String.IsNullOrEmpty uid || uid.Length <> 32 then false
    else
        let mutable ok = true
        let mutable i = 0
        while ok && i < uid.Length do
            let c = uid.[i]
            if not ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
            then ok <- false
            i <- i + 1
        ok

/// Anonymous user identity middleware.
/// Each visitor gets an `sp_uid` cookie containing a random GUID on first request.
/// Subsequent requests reuse it. Malformed cookie values (anything that isn't
/// a 32-char hex string) are treated as missing and replaced — defense against
/// cookie squatting and oversized values used to bloat SQL parameters.
let private spUidMiddleware (ctx: HttpContext) (next: RequestDelegate) =
    task {
        let presented =
            match ctx.Request.Cookies.TryGetValue(UserContext.cookieName) with
            | true, v when not (String.IsNullOrWhiteSpace v) -> v
            | _ -> ""

        let mutable uid = if isValidUid presented then presented else ""

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

    // Hard request-body size cap. The largest legitimate RPC payload is a
    // few hundred bytes (PantryItemInput JSON); 64KB is two orders of
    // magnitude over that and well under any spam attempt.
    builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
        fun (opts: Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions) ->
            opts.Limits.MaxRequestBodySize <- System.Nullable(64L * 1024L))
    |> ignore

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
