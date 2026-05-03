namespace SmartPantry

open System
open System.Data
open System.IO
open Microsoft.Data.Sqlite
open Dapper

/// SQLite + Dapper persistence layer for pantry items.
/// All queries are scoped by UserId (the cookie GUID), enforced server-side.
module Database =

    /// Path to the SQLite database. Comes from the DB_PATH env var, falling back to
    /// a local file for dev runs.
    let dbPath () =
        match Environment.GetEnvironmentVariable("DB_PATH") with
        | null | "" -> Path.Combine(AppContext.BaseDirectory, "smartpantry.db")
        | p -> p

    let private connectionString () =
        SqliteConnectionStringBuilder(
            DataSource = dbPath (),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        ).ToString()

    /// Open a fresh connection. Caller disposes.
    let openConnection () : IDbConnection =
        let conn = new SqliteConnection(connectionString ())
        conn.Open()
        upcast conn

    // ------------------------------------------------------------------
    // F# Option<'T> ↔ Dapper type handlers
    //
    // Dapper does not natively understand F# Option. Without these handlers the
    // mapper crashes on DBNull <-> None conversion. Register exactly once at
    // startup.
    // ------------------------------------------------------------------

    /// Generic Option handler — works for primitives that SQLite returns in
    /// the same .NET type (string, int, float).
    type private OptionHandler<'T>() =
        inherit SqlMapper.TypeHandler<'T option>()

        override _.SetValue(parameter, value) =
            parameter.Value <-
                match value with
                | Some v -> box v
                | None -> box DBNull.Value

        override _.Parse(value) =
            if isNull value || value = box DBNull.Value
            then None
            else Some (value :?> 'T)

    /// SQLite stores DateTime as TEXT, so we need an explicit string -> DateTime
    /// conversion on Parse. The generic OptionHandler<'T> would `:?> DateTime`
    /// the raw string and crash with InvalidCastException.
    type private DateTimeOptionHandler() =
        inherit SqlMapper.TypeHandler<DateTime option>()

        override _.SetValue(parameter, value) =
            parameter.Value <-
                match value with
                | Some (d: DateTime) -> box (d.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                | None -> box DBNull.Value

        override _.Parse(value) =
            if isNull value || value = box DBNull.Value then None
            else
                match value with
                | :? DateTime as d -> Some d
                | :? string as s ->
                    let mutable parsed = DateTime.MinValue
                    if DateTime.TryParse(s, &parsed) then Some parsed else None
                | _ -> None

    let mutable private handlersRegistered = false

    let private registerTypeHandlers () =
        if not handlersRegistered then
            SqlMapper.AddTypeHandler(OptionHandler<string>())
            SqlMapper.AddTypeHandler(OptionHandler<int>())
            SqlMapper.AddTypeHandler(OptionHandler<float>())
            SqlMapper.AddTypeHandler(DateTimeOptionHandler())
            handlersRegistered <- true

    // ------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------

    let private schemaSql = """
        CREATE TABLE IF NOT EXISTS Ingredients (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId      TEXT    NOT NULL,
            Name        TEXT    NOT NULL,
            Quantity    REAL    NOT NULL,
            Unit        TEXT    NOT NULL,
            ExpiryDate  TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_Ingredients_UserId
            ON Ingredients (UserId);

        PRAGMA journal_mode = WAL;
    """

    /// Initialize SQLite — create the file (if missing), tables, and indexes.
    /// Safe to call repeatedly.
    let init () =
        registerTypeHandlers ()
        let path = dbPath ()
        let dir = Path.GetDirectoryName(path)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore
        use conn = openConnection ()
        conn.Execute(schemaSql) |> ignore

    // ------------------------------------------------------------------
    // Queries — all scoped by userId (the cookie GUID)
    // ------------------------------------------------------------------

    /// All ingredients for a user, newest first.
    let getItems (userId: string) : PantryItem list =
        use conn = openConnection ()
        conn.Query<PantryItem>(
            "SELECT Id, UserId, Name, Quantity, Unit, ExpiryDate
             FROM Ingredients
             WHERE UserId = @UserId
             ORDER BY Id DESC",
            {| UserId = userId |})
        |> List.ofSeq

    /// Insert a new ingredient. Returns the row with its generated Id.
    let addItem (userId: string) (input: PantryItemInput) : PantryItem =
        use conn = openConnection ()
        let id =
            conn.ExecuteScalar<int64>(
                "INSERT INTO Ingredients (UserId, Name, Quantity, Unit, ExpiryDate)
                 VALUES (@UserId, @Name, @Quantity, @Unit, @ExpiryDate);
                 SELECT last_insert_rowid();",
                {| UserId = userId
                   Name = input.Name
                   Quantity = input.Quantity
                   Unit = input.Unit
                   ExpiryDate = input.ExpiryDate |})
        {
            Id = int id
            UserId = userId
            Name = input.Name
            Quantity = input.Quantity
            Unit = input.Unit
            ExpiryDate = input.ExpiryDate
        }

    /// Delete a single ingredient. Enforces ownership: the row must belong to userId
    /// or the delete is a no-op (returns 0).
    let deleteItem (userId: string) (id: int) : int =
        use conn = openConnection ()
        conn.Execute(
            "DELETE FROM Ingredients WHERE Id = @Id AND UserId = @UserId",
            {| Id = id; UserId = userId |})

    /// Update name/quantity/unit/expiry of an existing ingredient. Ownership enforced.
    let updateItem (userId: string) (item: PantryItem) : int =
        use conn = openConnection ()
        conn.Execute(
            "UPDATE Ingredients
             SET Name = @Name, Quantity = @Quantity, Unit = @Unit, ExpiryDate = @ExpiryDate
             WHERE Id = @Id AND UserId = @UserId",
            {| Id = item.Id
               UserId = userId
               Name = item.Name
               Quantity = item.Quantity
               Unit = item.Unit
               ExpiryDate = item.ExpiryDate |})
