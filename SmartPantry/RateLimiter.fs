namespace SmartPantry

open System
open System.Collections.Concurrent

/// In-process per-key rate limiter using parallel sliding-window counters.
///
/// We track two windows simultaneously per (scope, key):
///   - a short window (e.g. 60 seconds) — catches burst spam
///   - a long  window (e.g. 3600 seconds) — catches sustained spam
///
/// Limits are calibrated to be invisible to humans and visible only to
/// scripts/Postman. The expensive endpoint (GenerateRecipes) uses much
/// tighter caps because each call burns OpenAI tokens.
///
/// Server-only — not annotated [<JavaScript>] on purpose.
module RateLimiter =

    /// One rolling window. We swap the window when `WindowStart` is older
    /// than `WindowSeconds`; until then `Count` is the running tally.
    type private Bucket = {
        mutable Count: int
        mutable WindowStart: DateTime
    }

    /// Configuration for a single window.
    type Limit = {
        WindowSeconds: float
        MaxRequests: int
    }

    /// Outcome of a rate-limit check. `RetryAfterSeconds` on `Denied` is a
    /// best-effort estimate: how long until the *first* (shortest) window
    /// would let the next request through.
    type Result =
        | Allowed
        | Denied of retryAfterSeconds: int

    let private newBucket () =
        { Count = 0; WindowStart = DateTime.UtcNow }

    // One flat dictionary; the scope is mixed into the key so different
    // RPC families get isolated counters even for the same user.
    let private buckets = ConcurrentDictionary<string, Bucket>()

    let private bucketKey (scope: string) (key: string) (windowSecs: float) =
        // Window length is part of the key so the recipes 1-min and 1-hour
        // caps for the same user end up in distinct buckets.
        sprintf "%s|%g|%s" scope windowSecs key

    /// Lazy sweep — when the dictionary grows beyond a threshold, drop any
    /// bucket whose window started more than 2 hours ago. Keeps memory
    /// bounded without a dedicated background timer.
    let private maybeSweep () =
        if buckets.Count > 5000 then
            let cutoff = DateTime.UtcNow.AddHours(-2.0)
            for kvp in buckets do
                if kvp.Value.WindowStart < cutoff then
                    buckets.TryRemove(kvp.Key) |> ignore

    /// Atomic increment + check against ONE window. Always counts the
    /// current request (we want the spammer's traffic to be reflected in
    /// the bucket even when they're already over the limit, so subsequent
    /// requests see the same denial).
    let private checkOne (scope: string) (key: string) (limit: Limit) : Result =
        let now = DateTime.UtcNow
        let k = bucketKey scope key limit.WindowSeconds
        let b = buckets.GetOrAdd(k, fun _ -> newBucket ())
        lock b (fun () ->
            let elapsed = (now - b.WindowStart).TotalSeconds
            if elapsed >= limit.WindowSeconds then
                // Window rolled — start fresh, this request counts as 1.
                b.WindowStart <- now
                b.Count <- 1
                Allowed
            elif b.Count < limit.MaxRequests then
                b.Count <- b.Count + 1
                Allowed
            else
                let retry = max 1 (int (limit.WindowSeconds - elapsed) + 1)
                Denied retry)

    /// Check the request against EVERY window in the policy. The request
    /// is denied if any window is full; the returned retry-after is the
    /// shortest "how long until at least one window opens up" estimate.
    let check (scope: string) (key: string) (policy: Limit list) : Result =
        maybeSweep ()
        let mutable worst = Allowed
        for l in policy do
            match checkOne scope key l with
            | Denied r ->
                match worst with
                | Allowed -> worst <- Denied r
                | Denied prev -> if r < prev then worst <- Denied r
            | Allowed -> ()
        worst

    // ------------------------------------------------------------------
    // Predefined policies. Tuned so a real human will never trip them
    // (1 recipe/min average human, ~1 add/sec at most when bulk-loading).
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // We track two parallel policies for every endpoint:
    //   • perCookie — keyed on sp_uid; the attacker bound when they reuse
    //                 their cookie across spam requests.
    //   • perIp     — keyed on client IP; the attacker bound when they
    //                 try to escape the per-cookie cap by minting a fresh
    //                 cookie for every request.
    // Per-IP limits are deliberately much higher to stay invisible to
    // shared-NAT users (offices, carriers, dorms).
    // ------------------------------------------------------------------

    /// GenerateRecipes — the expensive endpoint. Each call burns OpenAI
    /// tokens, so the burst cap is tight. A real user clicks the button
    /// maybe a couple of times a minute at most.
    let recipesPerCookie : Limit list = [
        { WindowSeconds = 60.0;   MaxRequests = 5 }      // 5 / minute
        { WindowSeconds = 3600.0; MaxRequests = 30 }     // 30 / hour
    ]
    let recipesPerIp : Limit list = [
        { WindowSeconds = 60.0;   MaxRequests = 30 }     // 30 / minute total
        { WindowSeconds = 3600.0; MaxRequests = 200 }    // 200 / hour total
    ]

    /// AddItem / UpdateItem / DeleteItem / DeleteAll — cheap DB writes,
    /// but we still cap them to stop pure-DB spam.
    let writePerCookie : Limit list = [
        { WindowSeconds = 60.0;   MaxRequests = 60 }     // 60 / minute (~1/sec avg)
        { WindowSeconds = 3600.0; MaxRequests = 600 }    // 600 / hour
    ]
    let writePerIp : Limit list = [
        { WindowSeconds = 60.0;   MaxRequests = 300 }    // 300 / minute total
        { WindowSeconds = 3600.0; MaxRequests = 3000 }   // 3000 / hour total
    ]

    /// GetItems — read-only, cheap. Generous cap to avoid breaking polling
    /// patterns; real users only hit this on page load.
    let readPerCookie : Limit list = [
        { WindowSeconds = 60.0; MaxRequests = 120 }      // 120 / minute (2/sec)
    ]
    let readPerIp : Limit list = [
        { WindowSeconds = 60.0; MaxRequests = 600 }      // 600 / minute total
    ]

    /// Two-axis check: the request must pass BOTH the per-cookie and the
    /// per-IP policy. Returns the worst (smallest retry-after) when denied.
    let checkBoth (scope: string) (cookieKey: string) (ipKey: string)
                  (perCookie: Limit list) (perIp: Limit list) : Result =
        let a = check scope ("c:" + cookieKey) perCookie
        let b = check scope ("i:" + ipKey) perIp
        match a, b with
        | Allowed, Allowed -> Allowed
        | Denied r, Allowed
        | Allowed, Denied r -> Denied r
        | Denied r1, Denied r2 -> Denied (min r1 r2)
