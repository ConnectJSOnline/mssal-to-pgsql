# MSSQL to PostgreSQL Migration Tool

A .NET CLI tool to migrate database schema (tables, foreign keys, and views) from Microsoft SQL Server to PostgreSQL.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Access to source MSSQL and target PostgreSQL servers

## Usage

```bash
dotnet run -- "<mssql-connection-string>" "<pg-connection-string>" "<database>"
```

### Parameters

| Parameter | Required | Description |
|-----------|----------|-------------|
| `source` | Yes | MSSQL connection string (key-value format) |
| `target` | Yes | PostgreSQL connection string (key-value format) |
| `database` | Yes | Database name (applied to both source and target) |

### Example

```bash
dotnet run -- "Server=myserver,1433;user=sa;password=pass;TrustServerCertificate=True" "Host=pgserver;Port=5432;Username=postgres;Password=pass" "my-database"
```


## What It Does

1. **Tables** — Reads column metadata from `sys.columns`/`sys.types` in MSSQL and generates `CREATE TABLE` DDL for PostgreSQL. Maps MSSQL data types to PG equivalents (e.g. `NVARCHAR` → `VARCHAR`, `BIT` → `BOOLEAN`, `UNIQUEIDENTIFIER` → `UUID`, identity columns → `SERIAL`). Tables are created in the `public` schema. Skips tables that already exist.

2. **Foreign Keys** — Reads foreign key definitions from `sys.foreign_keys` and creates them in the target with `ON DELETE`/`ON UPDATE` actions. Supports composite keys. Skips FKs that already exist.

3. **Views** — Reads view definitions from `sys.sql_modules` and converts MSSQL SQL syntax to PostgreSQL:
   - `[dbo].` schema prefixes → removed
   - `[brackets]` → `"double quotes"`
   - `ISNULL` → `COALESCE`
   - `GETDATE()` → `NOW()`
   - `GETUTCDATE()` → `NOW() AT TIME ZONE 'UTC'`
   - `CONVERT(type, expr)` → `CAST(expr AS type)`
   - `TOP N` → `LIMIT N`
   - `CROSS APPLY` → `CROSS JOIN LATERAL`
   - `OUTER APPLY` → `LEFT JOIN LATERAL`
   - `WITH (NOLOCK)` → removed
   - `BIT` → `BOOLEAN`
   - `NVARCHAR(MAX)` → `TEXT`
   - Views depending on other views are retried automatically (up to 3 attempts).

## Type Mapping

| MSSQL | PostgreSQL |
|-------|------------|
| `INT` | `INTEGER` |
| `BIGINT` | `BIGINT` |
| `SMALLINT` | `SMALLINT` |
| `TINYINT` | `SMALLINT` |
| `BIT` | `BOOLEAN` |
| `FLOAT` | `DOUBLE PRECISION` |
| `REAL` | `REAL` |
| `DECIMAL`/`NUMERIC` | `NUMERIC(p,s)` |
| `MONEY` | `NUMERIC(19,4)` |
| `CHAR(n)` | `CHAR(n)` |
| `VARCHAR(n)` | `VARCHAR(n)` |
| `VARCHAR(MAX)` | `TEXT` |
| `NVARCHAR(n)` | `VARCHAR(n/2)` |
| `NVARCHAR(MAX)` | `TEXT` |
| `TEXT`/`NTEXT` | `TEXT` |
| `DATE` | `DATE` |
| `DATETIME`/`DATETIME2` | `TIMESTAMP` |
| `DATETIMEOFFSET` | `TIMESTAMPTZ` |
| `TIME` | `TIME` |
| `UNIQUEIDENTIFIER` | `UUID` |
| `VARBINARY`/`IMAGE` | `BYTEA` |
| `XML` | `XML` |
| Identity columns | `SERIAL`/`BIGSERIAL`/`SMALLSERIAL` |

## Notes

- The tool is **idempotent** — running it multiple times will skip already-existing objects.
- Data migration is **not included** — this tool only migrates schema.
- An implicit cast (`integer → varchar`) is created in the target database to handle MSSQL's implicit type conversions in views.
- The `database` parameter overrides any database specified in the connection strings.

## License

MIT

