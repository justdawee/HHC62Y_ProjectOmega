namespace SmartPantry

open WebSharper

/// Static catalog of common pantry ingredients used to drive the
/// autocomplete dropdown and the auto-unit feature. Each entry carries
/// English + Hungarian display names, a default unit, and an emoji icon.
[<JavaScript>]
module Catalog =

    type Suggestion = {
        En:   string
        Hu:   string
        Unit: string
        Icon: string
    }

    /// Curated list — we keep it human-sized so it fits in memory and the
    /// fuzzy match is fast even on every keystroke. Order matters: more
    /// common / more useful items go first so they win ties.
    let all : Suggestion list = [
        { En = "Eggs";              Hu = "Tojás";              Unit = "pcs";  Icon = "🥚" }
        { En = "Milk";              Hu = "Tej";                Unit = "l";    Icon = "🥛" }
        { En = "Butter";            Hu = "Vaj";                Unit = "g";    Icon = "🧈" }
        { En = "Cheese";            Hu = "Sajt";               Unit = "g";    Icon = "🧀" }
        { En = "Parmesan";          Hu = "Parmezán";           Unit = "g";    Icon = "🧀" }
        { En = "Yogurt";            Hu = "Joghurt";            Unit = "g";    Icon = "🥛" }
        { En = "Kefir";             Hu = "Kefír";              Unit = "ml";   Icon = "🥛" }
        { En = "Cream";             Hu = "Tejszín";            Unit = "ml";   Icon = "🥛" }
        { En = "Sour cream";        Hu = "Tejföl";             Unit = "g";    Icon = "🥛" }

        { En = "Flour";             Hu = "Liszt";              Unit = "g";    Icon = "🌾" }
        { En = "Bread";             Hu = "Kenyér";             Unit = "g";    Icon = "🍞" }
        { En = "Rice";              Hu = "Rizs";               Unit = "g";    Icon = "🍚" }
        { En = "Arborio rice";      Hu = "Arborio rizs";       Unit = "g";    Icon = "🍚" }
        { En = "Pasta";             Hu = "Tészta";             Unit = "g";    Icon = "🍝" }
        { En = "Spaghetti";         Hu = "Spagetti";           Unit = "g";    Icon = "🍝" }
        { En = "Sugar";             Hu = "Cukor";              Unit = "g";    Icon = "🍬" }
        { En = "Honey";             Hu = "Méz";                Unit = "g";    Icon = "🍯" }
        { En = "Salt";              Hu = "Só";                 Unit = "g";    Icon = "🧂" }
        { En = "Pepper";            Hu = "Bors";               Unit = "g";    Icon = "🧂" }
        { En = "Oil";               Hu = "Olaj";               Unit = "ml";   Icon = "🫒" }
        { En = "Olive oil";         Hu = "Olívaolaj";          Unit = "ml";   Icon = "🫒" }
        { En = "Vinegar";           Hu = "Ecet";               Unit = "ml";   Icon = "🧴" }

        { En = "Onion";             Hu = "Hagyma";             Unit = "pcs";  Icon = "🧅" }
        { En = "Yellow onion";      Hu = "Vöröshagyma";        Unit = "pcs";  Icon = "🧅" }
        { En = "Red onion";         Hu = "Lila hagyma";        Unit = "pcs";  Icon = "🧅" }
        { En = "Garlic";            Hu = "Fokhagyma";          Unit = "pcs";  Icon = "🧄" }
        { En = "Tomato";            Hu = "Paradicsom";         Unit = "pcs";  Icon = "🍅" }
        { En = "Cherry tomatoes";   Hu = "Koktélparadicsom";   Unit = "g";    Icon = "🍅" }
        { En = "Potato";            Hu = "Burgonya";           Unit = "pcs";  Icon = "🥔" }
        { En = "Carrot";            Hu = "Sárgarépa";          Unit = "pcs";  Icon = "🥕" }
        { En = "Mushrooms";         Hu = "Gomba";              Unit = "g";    Icon = "🍄" }
        { En = "Cremini mushrooms"; Hu = "Cremini gomba";      Unit = "g";    Icon = "🍄" }
        { En = "Bell pepper";       Hu = "Kaliforniai paprika";Unit = "pcs";  Icon = "🫑" }
        { En = "Paprika";           Hu = "Paprika";            Unit = "pcs";  Icon = "🌶️" }
        { En = "Chili";             Hu = "Csili";              Unit = "pcs";  Icon = "🌶️" }
        { En = "Lettuce";           Hu = "Saláta";             Unit = "g";    Icon = "🥬" }
        { En = "Spinach";           Hu = "Spenót";             Unit = "g";    Icon = "🥬" }
        { En = "Broccoli";          Hu = "Brokkoli";           Unit = "g";    Icon = "🥦" }
        { En = "Cucumber";          Hu = "Uborka";             Unit = "pcs";  Icon = "🥒" }
        { En = "Corn";              Hu = "Kukorica";           Unit = "g";    Icon = "🌽" }
        { En = "Zucchini";          Hu = "Cukkini";            Unit = "pcs";  Icon = "🥒" }
        { En = "Eggplant";          Hu = "Padlizsán";          Unit = "pcs";  Icon = "🍆" }
        { En = "Avocado";           Hu = "Avokádó";            Unit = "pcs";  Icon = "🥑" }
        { En = "Beans";             Hu = "Bab";                Unit = "g";    Icon = "🫘" }
        { En = "Lentils";           Hu = "Lencse";             Unit = "g";    Icon = "🫘" }
        { En = "Chickpeas";         Hu = "Csicseriborsó";      Unit = "g";    Icon = "🫘" }
        { En = "Peas";              Hu = "Borsó";              Unit = "g";    Icon = "🫛" }

        { En = "Chicken";           Hu = "Csirke";             Unit = "g";    Icon = "🍗" }
        { En = "Chicken breast";    Hu = "Csirkemell";         Unit = "g";    Icon = "🍗" }
        { En = "Beef";              Hu = "Marha";              Unit = "g";    Icon = "🥩" }
        { En = "Pork";              Hu = "Sertés";             Unit = "g";    Icon = "🥓" }
        { En = "Bacon";             Hu = "Szalonna";           Unit = "g";    Icon = "🥓" }
        { En = "Ham";               Hu = "Sonka";              Unit = "g";    Icon = "🥓" }
        { En = "Sausage";           Hu = "Kolbász";            Unit = "g";    Icon = "🌭" }
        { En = "Fish";              Hu = "Hal";                Unit = "g";    Icon = "🐟" }
        { En = "Salmon";            Hu = "Lazac";              Unit = "g";    Icon = "🐟" }
        { En = "Shrimp";            Hu = "Garnéla";            Unit = "g";    Icon = "🦐" }
        { En = "Tuna";              Hu = "Tonhal";             Unit = "g";    Icon = "🐟" }

        { En = "Lemon";             Hu = "Citrom";             Unit = "pcs";  Icon = "🍋" }
        { En = "Lime";              Hu = "Lime";               Unit = "pcs";  Icon = "🍋" }
        { En = "Apple";             Hu = "Alma";               Unit = "pcs";  Icon = "🍎" }
        { En = "Banana";            Hu = "Banán";              Unit = "pcs";  Icon = "🍌" }
        { En = "Orange";            Hu = "Narancs";            Unit = "pcs";  Icon = "🍊" }
        { En = "Strawberry";        Hu = "Eper";               Unit = "g";    Icon = "🍓" }
        { En = "Blueberry";         Hu = "Áfonya";             Unit = "g";    Icon = "🫐" }
        { En = "Grape";             Hu = "Szőlő";              Unit = "g";    Icon = "🍇" }
        { En = "Pear";              Hu = "Körte";              Unit = "pcs";  Icon = "🍐" }

        { En = "Basil";             Hu = "Bazsalikom";         Unit = "g";    Icon = "🌿" }
        { En = "Parsley";           Hu = "Petrezselyem";       Unit = "g";    Icon = "🌿" }
        { En = "Thyme";             Hu = "Kakukkfű";           Unit = "g";    Icon = "🌿" }
        { En = "Oregano";           Hu = "Oregánó";            Unit = "g";    Icon = "🌿" }
        { En = "Rosemary";          Hu = "Rozmaring";          Unit = "g";    Icon = "🌿" }

        { En = "Chocolate";         Hu = "Csokoládé";          Unit = "g";    Icon = "🍫" }
        { En = "Cocoa";             Hu = "Kakaó";              Unit = "g";    Icon = "🍫" }
        { En = "Vanilla";           Hu = "Vanília";            Unit = "g";    Icon = "🌼" }
    ]

    /// Lower-case + strip Hungarian accents so a "tö" search hits "to".
    let private normalize (s: string) : string =
        if isNull s then ""
        else
            (s.ToLower())
                .Replace("á", "a").Replace("é", "e").Replace("í", "i")
                .Replace("ó", "o").Replace("ö", "o").Replace("ő", "o")
                .Replace("ú", "u").Replace("ü", "u").Replace("ű", "u")
                .Trim()

    /// Returns up to `max` suggestions whose name starts-with the query in
    /// either language; falls back to a "contains" match. Sorted by quality
    /// (prefix matches first, length tie-breaker).
    let suggest (lang: Lang) (query: string) (max: int) : Suggestion list =
        let q = normalize query
        if q.Length < 1 then []
        else
            let display (s: Suggestion) =
                match lang with En -> s.En | Hu -> s.Hu

            let scored =
                all
                |> List.collect (fun s ->
                    let nEn = normalize s.En
                    let nHu = normalize s.Hu
                    let starts = nEn.StartsWith(q) || nHu.StartsWith(q)
                    let contains = nEn.Contains(q) || nHu.Contains(q)
                    if starts then [ (s, 0, (display s).Length) ]
                    elif contains then [ (s, 1, (display s).Length) ]
                    else [])
                |> List.sortBy (fun (_, prio, len) -> prio * 1000 + len)
                |> List.map (fun (s, _, _) -> s)

            scored |> List.truncate max

    /// Look up the catalog entry that exactly matches the given name in
    /// either language (case + accent insensitive). Used when a user picks
    /// a suggestion or types a known name and we want to auto-fill the unit.
    let findByName (name: string) : Suggestion option =
        let n = normalize name
        all |> List.tryFind (fun s ->
            normalize s.En = n || normalize s.Hu = n)
