using Microsoft.Data.SqlClient;
using Npgsql;

namespace mssql_pg_migration;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: mssql-pg-migration <source> <target>");
            return;
        }

        var source = args[0];
        var target = args[1];

        // Extract database name from source connection string
        var sourceBuilder = new SqlConnectionStringBuilder(source);
        var database = sourceBuilder.InitialCatalog;

        if (string.IsNullOrEmpty(database))
        {
            Console.WriteLine("Error: Source connection string must include a Database (Initial Catalog).");
            return;
        }

        Console.WriteLine($"Source: {source}");
        Console.WriteLine($"Target: {target}");
        Console.WriteLine($"Database: {database}");

        // Create target database if it doesn't exist
        EnsureTargetDatabaseExists(target, database);

        Console.WriteLine("Creating all tables...");
        CreateAllTables(source, target, database);

        // Copy foreign keys
        Console.WriteLine("\nCreating foreign keys...");
        CopyForeignKeys(source, target, database);

        // Copy views
        Console.WriteLine("\nCreating views...");
        CopyViews(source, target, database);
    }

    static void EnsureTargetDatabaseExists(string target, string database)
    {
        // Connect to the default 'postgres' database to check/create the target database
        var builder = new NpgsqlConnectionStringBuilder(target) { Database = "postgres" };
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();

        var checkQuery = "SELECT 1 FROM pg_database WHERE datname = @dbname";
        using var checkCmd = new NpgsqlCommand(checkQuery, connection);
        checkCmd.Parameters.AddWithValue("@dbname", database);
        var exists = checkCmd.ExecuteScalar() != null;

        if (!exists)
        {
            // Database names can't be parameterized in CREATE DATABASE
            using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            createCmd.ExecuteNonQuery();
            Console.WriteLine($"Database '{database}' created in target server.");
        }
        else
        {
            Console.WriteLine($"Database '{database}' already exists in target server.");
        }
    }

    static void CreateAllTables(string source, string target, string database)
    {
        var tables = GetAllSourceTables(source, database);

        if (tables.Count == 0)
        {
            Console.WriteLine("No tables found in the source database.");
            return;
        }

        Console.WriteLine($"Found {tables.Count} table(s) in source database.");

        foreach (var (schema, tableName) in tables)
        {
            var fullName = $"{schema}.{tableName}";
            CreateTableIfNotExists(source, target, database, fullName, schema, tableName);
        }
    }

    static void CreateTableIfNotExists(string source, string target, string database, string table, string? schema = null, string? tableName = null)
    {
        // Parse schema and table name if not provided
        if (schema == null || tableName == null)
        {
            var parts = table.Split('.');
            if (parts.Length == 2)
            {
                schema = parts[0];
                tableName = parts[1];
            }
            else
            {
                schema = "dbo";
                tableName = table;
            }
        }

        // Generate the CREATE TABLE DDL from source MSSQL schema
        var ddl = GenerateCreateTableDDL(source, database, schema, tableName);

        if (string.IsNullOrEmpty(ddl))
        {
            Console.WriteLine($"Table '{schema}.{tableName}' does not exist in the source database.");
            return;
        }

        // Check if the table exists in the target PostgreSQL database
        var pgSchema = "public";
        var pgTable = tableName.ToLower();

        if (!TargetTableExists(target, pgSchema, pgTable))
        {
            CreateTargetTable(target, pgSchema, ddl);
            Console.WriteLine($"Table '{pgSchema}.{pgTable}' created in the target database.");
        }
        else
        {
            Console.WriteLine($"Table '{pgSchema}.{pgTable}' already exists in the target database.");
        }
    }

    static List<(string Schema, string Table)> GetAllSourceTables(string source, string database)
    {
        var tables = new List<(string, string)>();

        using var connection = new SqlConnection(source);
        connection.Open();
        connection.ChangeDatabase(database);

        var query = @"SELECT s.name AS schema_name, t.name AS table_name 
                      FROM sys.tables t 
                      INNER JOIN sys.schemas s ON t.schema_id = s.schema_id 
                      ORDER BY s.name, t.name";

        using var command = new SqlCommand(query, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            tables.Add((reader["schema_name"].ToString()!, reader["table_name"].ToString()!));
        }

        return tables;
    }

    static string? GenerateCreateTableDDL(string source, string database, string schema, string table)
    {
        using var connection = new SqlConnection(source);
        connection.Open();
        connection.ChangeDatabase(database);

        // Verify table exists
        var checkQuery = @"SELECT OBJECT_ID(@fullName) AS obj_id";
        using var checkCmd = new SqlCommand(checkQuery, connection);
        checkCmd.Parameters.AddWithValue("@fullName", $"{schema}.{table}");
        var objId = checkCmd.ExecuteScalar();
        if (objId == null || objId == DBNull.Value)
            return null;

        // Get columns
        var columnsQuery = @"
            SELECT 
                c.name AS column_name,
                tp.name AS data_type,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                ic.seed_value,
                ic.increment_value,
                dc.definition AS default_value
            FROM sys.columns c
            INNER JOIN sys.types tp ON c.user_type_id = tp.user_type_id
            LEFT JOIN sys.identity_columns ic ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
            WHERE c.object_id = OBJECT_ID(@fullName)
            ORDER BY c.column_id";

        using var colCmd = new SqlCommand(columnsQuery, connection);
        colCmd.Parameters.AddWithValue("@fullName", $"{schema}.{table}");

        var columns = new List<string>();
        using var reader = colCmd.ExecuteReader();

        while (reader.Read())
        {
            var colName = reader["column_name"].ToString()!.ToLower();
            var dataType = reader["data_type"].ToString()!;
            var maxLength = Convert.ToInt32(reader["max_length"]);
            var precision = Convert.ToByte(reader["precision"]);
            var scale = Convert.ToByte(reader["scale"]);
            var isNullable = Convert.ToBoolean(reader["is_nullable"]);
            var isIdentity = Convert.ToBoolean(reader["is_identity"]);

            var pgType = MapMssqlTypeToPg(dataType, maxLength, precision, scale, isIdentity);
            var nullable = isNullable ? "" : " NOT NULL";

            columns.Add($"    \"{colName}\" {pgType}{nullable}");
        }

        if (columns.Count == 0)
            return null;

        reader.Close();

        // Get primary key
        var pkQuery = @"
            SELECT col.name AS column_name
            FROM sys.indexes idx
            INNER JOIN sys.index_columns ic ON idx.object_id = ic.object_id AND idx.index_id = ic.index_id
            INNER JOIN sys.columns col ON ic.object_id = col.object_id AND ic.column_id = col.column_id
            WHERE idx.object_id = OBJECT_ID(@fullName) AND idx.is_primary_key = 1
            ORDER BY ic.key_ordinal";

        using var pkCmd = new SqlCommand(pkQuery, connection);
        pkCmd.Parameters.AddWithValue("@fullName", $"{schema}.{table}");
        using var pkReader = pkCmd.ExecuteReader();

        var pkColumns = new List<string>();
        while (pkReader.Read())
        {
            pkColumns.Add($"\"{pkReader["column_name"].ToString()!.ToLower()}\"");
        }

        var pgSchema = "public";
        var pgTable = table.ToLower();

        var ddl = $"CREATE TABLE IF NOT EXISTS \"{pgSchema}\".\"{pgTable}\" (\n";
        ddl += string.Join(",\n", columns);

        if (pkColumns.Count > 0)
        {
            ddl += $",\n    PRIMARY KEY ({string.Join(", ", pkColumns)})";
        }

        ddl += "\n)";

        return ddl;
    }

    static string MapMssqlTypeToPg(string mssqlType, int maxLength, byte precision, byte scale, bool isIdentity)
    {
        if (isIdentity)
        {
            return mssqlType.ToLower() switch
            {
                "bigint" => "BIGSERIAL",
                "smallint" => "SMALLSERIAL",
                _ => "SERIAL"
            };
        }

        return mssqlType.ToLower() switch
        {
            "int" => "INTEGER",
            "bigint" => "BIGINT",
            "smallint" => "SMALLINT",
            "tinyint" => "SMALLINT",
            "bit" => "BOOLEAN",
            "float" => "DOUBLE PRECISION",
            "real" => "REAL",
            "decimal" or "numeric" => $"NUMERIC({precision},{scale})",
            "money" => "NUMERIC(19,4)",
            "smallmoney" => "NUMERIC(10,4)",
            "char" => $"CHAR({maxLength})",
            "nchar" => $"CHAR({maxLength / 2})",
            "varchar" => maxLength == -1 ? "TEXT" : $"VARCHAR({maxLength})",
            "nvarchar" => maxLength == -1 ? "TEXT" : $"VARCHAR({maxLength / 2})",
            "text" or "ntext" => "TEXT",
            "date" => "DATE",
            "datetime" or "datetime2" or "smalldatetime" => "TIMESTAMP",
            "datetimeoffset" => "TIMESTAMPTZ",
            "time" => "TIME",
            "uniqueidentifier" => "UUID",
            "varbinary" or "binary" or "image" => "BYTEA",
            "xml" => "XML",
            _ => "TEXT"
        };
    }

    static bool TargetTableExists(string target, string schema, string table)
    {
        using var connection = new NpgsqlConnection(target);
        connection.Open();
        var query = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table)";

        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        return (bool)command.ExecuteScalar()!;
    }

    static void CreateTargetTable(string target, string schema, string ddl)
    {
        using var connection = new NpgsqlConnection(target);
        connection.Open();


        using var command = new NpgsqlCommand(ddl, connection);
        command.ExecuteNonQuery();
    }

    static void CopyForeignKeys(string source, string target, string database)
    {
        var foreignKeys = GetSourceForeignKeys(source, database);

        if (foreignKeys.Count == 0)
        {
            Console.WriteLine("No foreign keys found.");
            return;
        }

        Console.WriteLine($"Found {foreignKeys.Count} foreign key(s).");

        using var connection = new NpgsqlConnection(target);
        connection.Open();

        foreach (var fk in foreignKeys)
        {
            // Check if FK already exists
            var checkQuery = "SELECT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE constraint_name = @name AND constraint_type = 'FOREIGN KEY')";
            using var checkCmd = new NpgsqlCommand(checkQuery, connection);
            checkCmd.Parameters.AddWithValue("@name", fk.ConstraintName.ToLower());
            var exists = (bool)checkCmd.ExecuteScalar()!;

            if (exists)
            {
                Console.WriteLine($"  FK '{fk.ConstraintName.ToLower()}' already exists. Skipping.");
                continue;
            }

            var fkColumns = string.Join(", ", fk.Columns.Select(c => $"\"{c.ToLower()}\""));
            var refColumns = string.Join(", ", fk.ReferencedColumns.Select(c => $"\"{c.ToLower()}\""));

            var ddl = $"ALTER TABLE \"public\".\"{fk.TableName.ToLower()}\" ADD CONSTRAINT \"{fk.ConstraintName.ToLower()}\" " +
                      $"FOREIGN KEY ({fkColumns}) REFERENCES \"public\".\"{fk.ReferencedTable.ToLower()}\" ({refColumns})";

            if (fk.DeleteAction != "NO_ACTION")
                ddl += $" ON DELETE {fk.DeleteAction.Replace("_", " ")}";
            if (fk.UpdateAction != "NO_ACTION")
                ddl += $" ON UPDATE {fk.UpdateAction.Replace("_", " ")}";

            try
            {
                using var cmd = new NpgsqlCommand(ddl, connection);
                cmd.ExecuteNonQuery();
                Console.WriteLine($"  FK '{fk.ConstraintName.ToLower()}' created on '{fk.TableName.ToLower()}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FK '{fk.ConstraintName.ToLower()}' failed: {ex.Message}");
            }
        }
    }

    static List<ForeignKeyInfo> GetSourceForeignKeys(string source, string database)
    {
        var foreignKeys = new Dictionary<string, ForeignKeyInfo>();

        using var connection = new SqlConnection(source);
        connection.Open();
        connection.ChangeDatabase(database);

        var query = @"
            SELECT 
                fk.name AS fk_name,
                tp.name AS parent_table,
                tr.name AS referenced_table,
                cp.name AS parent_column,
                cr.name AS referenced_column,
                fk.delete_referential_action_desc AS delete_action,
                fk.update_referential_action_desc AS update_action
            FROM sys.foreign_keys fk
            INNER JOIN sys.tables tp ON fk.parent_object_id = tp.object_id
            INNER JOIN sys.tables tr ON fk.referenced_object_id = tr.object_id
            INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            INNER JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
            INNER JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id
            ORDER BY fk.name, fkc.constraint_column_id";

        using var command = new SqlCommand(query, connection);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var fkName = reader["fk_name"].ToString()!;
            if (!foreignKeys.ContainsKey(fkName))
            {
                foreignKeys[fkName] = new ForeignKeyInfo
                {
                    ConstraintName = fkName,
                    TableName = reader["parent_table"].ToString()!,
                    ReferencedTable = reader["referenced_table"].ToString()!,
                    DeleteAction = reader["delete_action"].ToString()!,
                    UpdateAction = reader["update_action"].ToString()!,
                    Columns = new List<string>(),
                    ReferencedColumns = new List<string>()
                };
            }

            foreignKeys[fkName].Columns.Add(reader["parent_column"].ToString()!);
            foreignKeys[fkName].ReferencedColumns.Add(reader["referenced_column"].ToString()!);
        }

        return foreignKeys.Values.ToList();
    }

    static void CopyViews(string source, string target, string database)
    {
        var views = GetSourceViews(source, database);

        if (views.Count == 0)
        {
            Console.WriteLine("No views found.");
            return;
        }

        Console.WriteLine($"Found {views.Count} view(s).");

        using var connection = new NpgsqlConnection(target);
        connection.Open();

        // Create implicit cast from integer to varchar (MSSQL allows this implicitly)
        try
        {
            using var castCmd = new NpgsqlCommand(@"
                CREATE OR REPLACE FUNCTION pg_catalog.int4_to_text(integer) RETURNS text AS $$ SELECT $1::text $$ LANGUAGE sql IMMUTABLE;
                DO $$ BEGIN
                    CREATE CAST (integer AS character varying) WITH FUNCTION pg_catalog.int4_to_text(integer) AS IMPLICIT;
                EXCEPTION WHEN duplicate_object THEN NULL;
                END $$;", connection);
            castCmd.ExecuteNonQuery();
            Console.WriteLine("  Implicit cast (integer -> varchar) created.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Implicit cast setup skipped: {ex.Message}");
        }

        // Retry loop to handle dependency ordering
        var pending = new List<(string Name, string Definition)>(views);
        var maxRetries = 3;

        for (var attempt = 0; attempt < maxRetries && pending.Count > 0; attempt++)
        {
            if (attempt > 0)
                Console.WriteLine($"  Retrying {pending.Count} failed view(s) (attempt {attempt + 1})...");

            var failed = new List<(string Name, string Definition)>();

            foreach (var (viewName, definition) in pending)
            {
                var pgViewName = viewName.ToLower();

                // Check if view already exists
                var checkQuery = "SELECT EXISTS (SELECT 1 FROM information_schema.views WHERE table_schema = 'public' AND table_name = @name)";
                using var checkCmd = new NpgsqlCommand(checkQuery, connection);
                checkCmd.Parameters.AddWithValue("@name", pgViewName);
                var exists = (bool)checkCmd.ExecuteScalar()!;

                if (exists)
                {
                    Console.WriteLine($"  View '{pgViewName}' already exists. Skipping.");
                    continue;
                }

                var pgDefinition = ConvertViewDefinitionToPg(definition, pgViewName);

                try
                {
                    using var cmd = new NpgsqlCommand(pgDefinition, connection);
                    cmd.ExecuteNonQuery();
                    Console.WriteLine($"  View '{pgViewName}' created.");
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries - 1)
                        failed.Add((viewName, definition));
                    else
                    {
                        Console.WriteLine($"  View '{pgViewName}' failed: {ex.Message}");
                        Console.WriteLine($"  DDL:\n{pgDefinition}\n");
                    }
                }
            }

            pending = failed;
        }
    }

    static List<(string Name, string Definition)> GetSourceViews(string source, string database)
    {
        var views = new List<(string, string)>();

        using var connection = new SqlConnection(source);
        connection.Open();
        connection.ChangeDatabase(database);

        var query = @"
            SELECT v.name AS view_name, m.definition AS view_definition
            FROM sys.views v
            INNER JOIN sys.sql_modules m ON v.object_id = m.object_id
            ORDER BY v.name";

        using var command = new SqlCommand(query, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var name = reader["view_name"].ToString()!;
            var definition = reader["view_definition"].ToString()!;
            views.Add((name, definition));
        }

        return views;
    }

    static string ConvertViewDefinitionToPg(string mssqlDefinition, string pgViewName)
    {
        var def = mssqlDefinition;

        // Remove schema prefixes like [dbo]. or dbo.
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\[?dbo\]?\.", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove square brackets
        def = def.Replace("[", "\"").Replace("]", "\"");

        // Replace CROSS APPLY with CROSS JOIN LATERAL
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bCROSS\s+APPLY\b", "CROSS JOIN LATERAL", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace OUTER APPLY with LEFT JOIN LATERAL ... ON TRUE
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bOUTER\s+APPLY\b", "LEFT JOIN LATERAL", System.Text.RegularExpressions.RegexOptions.IgnoreCase);


        // Replace ISNULL with COALESCE
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bISNULL\b", "COALESCE", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace GETDATE() with NOW()
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bGETDATE\(\)", "NOW()", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace GETUTCDATE() with NOW() AT TIME ZONE 'UTC'
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bGETUTCDATE\(\)", "NOW() AT TIME ZONE 'UTC'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace TOP N with LIMIT N (simple cases)
        var topMatch = System.Text.RegularExpressions.Regex.Match(def, @"\bTOP\s+(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (topMatch.Success)
        {
            def = System.Text.RegularExpressions.Regex.Replace(def, @"\bTOP\s+\d+\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            def = def.TrimEnd() + $" LIMIT {topMatch.Groups[1].Value}";
        }

        // Replace NVARCHAR/VARCHAR casting with TEXT
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bN?VARCHAR\s*\(\s*MAX\s*\)", "TEXT", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove CONVERT - replace with simple CAST (handles 2-arg form only)
        // CONVERT(type, expr) -> CAST(expr AS type) — skip 3-arg style conversions
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bCONVERT\s*\(\s*([\w()]+)\s*,\s*([^,)]+)\s*\)", "CAST($2 AS $1)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Remove 3-arg CONVERT(type, expr, style) -> CAST(expr AS type)
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bCONVERT\s*\(\s*([\w()]+)\s*,\s*([^,)]+)\s*,\s*\d+\s*\)", "CAST($2 AS $1)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace CAST(... AS BIT) with CAST(... AS BOOLEAN)
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bBIT\b", "BOOLEAN", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace NVARCHAR/VARCHAR(n) with VARCHAR(n) in casts
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\bNVARCHAR\s*\(\s*(\d+)\s*\)", "VARCHAR($1)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Quote reserved words commonly used as column names
        def = System.Text.RegularExpressions.Regex.Replace(def, @"(?<=\.)(?i)(date|time|user|order|group|limit|offset)(?=\s|,|$|\))", "\"$1\"");

        // Cast both sides of = and != in JOIN ON and WHERE to varchar(100) to fix int/varchar mismatches
        def = System.Text.RegularExpressions.Regex.Replace(def,
            @"([\w.""]+)\s*(!=|(?<!=|!|<|>)=(?!=|>))\s*([\w.""]+)",
            "$1::varchar(100) $2 $3::varchar(100)",
            System.Text.RegularExpressions.RegexOptions.None);


        // Replace WITH (NOLOCK) hints
        def = System.Text.RegularExpressions.Regex.Replace(def, @"\s*WITH\s*\(\s*NOLOCK\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace CREATE VIEW statement to use pg name
        def = System.Text.RegularExpressions.Regex.Replace(def, @"CREATE\s+VIEW\s+\S+", $"CREATE VIEW \"public\".\"{pgViewName}\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return def;
    }
}

class ForeignKeyInfo
{
    public string ConstraintName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public string DeleteAction { get; set; } = "";
    public string UpdateAction { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<string> ReferencedColumns { get; set; } = new();
}