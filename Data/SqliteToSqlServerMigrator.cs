using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WebDS_Test.Data;

/// <summary>
/// One-time import of the SQLite development store into the SQL Server database.
/// </summary>
/// <remarks>
/// <para>
/// Both providers share the same <see cref="ApplicationDbContext"/> and therefore the same model,
/// so the import is provider-agnostic: every entity is read through the SQLite provider (which
/// performs the correct conversions out of SQLite's dynamic types) and re-inserted through the
/// SQL Server provider. Reading and writing through EF — rather than copying raw SQL — keeps
/// decimals, dates, booleans, enums and the <c>RowVersion</c> byte arrays faithful.
/// </para>
/// <para>
/// Primary keys are preserved deliberately: foreign keys are stored as plain properties, so
/// regenerating identities would silently break every relationship. Supplying a value for an
/// identity column requires <c>SET IDENTITY_INSERT</c>, which EF does not emit on its own — the
/// import turns it on per table, on the transaction's connection so the setting and the inserts
/// share a session.
/// </para>
/// <para>
/// Rows are written one entity type at a time, in dependency order, inside a single transaction,
/// and only when the destination is empty — running the import twice cannot duplicate data.
/// </para>
/// <para>
/// An entity whose table the source store never created is skipped rather than fatal: the store's
/// <c>Log</c> table is written only by the running application, so an older SQLite file has
/// nothing to contribute for it.
/// </para>
/// </remarks>
public static class SqliteToSqlServerMigrator
{
    /// <summary>Outcome of an import attempt, for logging.</summary>
    public sealed record MigrationSummary(bool Imported, int TotalRows, IReadOnlyList<(string Table, int Rows)> Tables)
    {
        public static readonly MigrationSummary Skipped = new(false, 0, Array.Empty<(string, int)>());
    }

    public static async Task<MigrationSummary> MigrateAsync(
        ApplicationDbContext destination,
        string sqlitePath,
        ILogger logger,
        CancellationToken ct = default)
    {
        // The destination must exist with its schema before anything can be inserted.
        await destination.Database.EnsureCreatedAsync(ct);

        // Idempotence guard: only ever import into an empty store.
        if (await destination.Products.AnyAsync(ct) || await destination.Users.AnyAsync(ct))
        {
            logger.LogInformation("SQL Server store already contains data; skipping the SQLite import.");
            return MigrationSummary.Skipped;
        }

        var sourceOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={sqlitePath}")
            .Options;

        await using var source = new ApplicationDbContext(sourceOptions);

        // A shadow foreign key lives only in the change tracker. Reading untracked instances and
        // re-attaching them would lose those values, so refuse loudly rather than import corrupt
        // relationships. The model is asserted to have none.
        foreach (var fk in source.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
        {
            if (fk.Properties.Any(p => p.IsShadowProperty()))
            {
                throw new InvalidOperationException(
                    $"Cannot import: relationship {fk.DeclaringEntityType.DisplayName()}.{string.Join("+", fk.Properties.Select(p => p.Name))} uses a shadow foreign key, which the untracked copy would drop.");
            }
        }

        var entityTypes = source.Model.GetEntityTypes()
            .Where(t => t.BaseType is null && !t.IsOwned() && t.FindPrimaryKey() is not null)
            .ToList();

        var counts = new List<(string Table, int Rows)>();
        var total = 0;

        // One transaction pins a single connection, which is what keeps SET IDENTITY_INSERT and
        // the inserts that follow on the same session. It also makes the import all-or-nothing.
        await using var transaction = await destination.Database.BeginTransactionAsync(ct);

        // Table names are bracket-quoted identifiers taken from the EF model (see QualifyTable), never
        // user input, so the interpolated SET IDENTITY_INSERT statements below are safe.
#pragma warning disable EF1002
        foreach (var entityType in SortByDependencies(entityTypes))
        {
            ct.ThrowIfCancellationRequested();

            List<object> rows;
            try
            {
                rows = await ReadRowsAsync(source, entityType.ClrType, ct);
            }
            catch (DbException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                // The SQLite store predates this table and has nothing to contribute. The Log
                // table — written only by the running application — is the expected case; a
                // table the model has but the source never created must not fail the whole import.
                logger.LogDebug(
                    "Skipping {Table}: the SQLite source has no such table.",
                    entityType.GetTableName() ?? entityType.ClrType.Name);
                continue;
            }

            if (rows.Count == 0)
            {
                continue;
            }

            var table = QualifyTable(entityType);
            var identityInsert = HasIdentityKey(entityType);

            if (identityInsert)
            {
                await destination.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} ON", ct);
            }

            try
            {
                await WriteRowsAsync(destination, entityType.ClrType, rows, ct);
            }
            finally
            {
                if (identityInsert)
                {
                    // Best effort: a failure here must not mask the original insert failure.
                    try
                    {
                        await destination.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} OFF", ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not turn IDENTITY_INSERT off again for {Table}.", table);
                    }
                }
            }

            counts.Add((entityType.GetTableName() ?? entityType.ClrType.Name, rows.Count));
            total += rows.Count;
        }
#pragma warning restore EF1002

        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Imported {TotalRows} rows from {Source} into SQL Server across {TableCount} tables.",
            total, sqlitePath, counts.Count);

        foreach (var (table, rows) in counts)
        {
            logger.LogInformation("  {Table,-22} {Rows}", table, rows);
        }

        return new MigrationSummary(true, total, counts);
    }

    /// <summary>
    /// Orders entity types so principals (the "one" side of a foreign key) precede dependents,
    /// which is what the foreign keys require. Self-references are ignored here: EF orders rows
    /// within a single table itself.
    /// </summary>
    private static List<IEntityType> SortByDependencies(List<IEntityType> types)
    {
        var membership = new HashSet<IEntityType>(types);
        var ordered = new List<IEntityType>();
        var visited = new HashSet<IEntityType>();

        void Visit(IEntityType type)
        {
            if (!visited.Add(type))
            {
                return;
            }

            foreach (var fk in type.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType;
                if (!ReferenceEquals(principal, type) && membership.Contains(principal))
                {
                    Visit(principal);
                }
            }

            ordered.Add(type);
        }

        foreach (var type in types)
        {
            Visit(type);
        }

        return ordered;
    }

    /// <summary>
    /// True when the single-column primary key is an integer generated by the database, i.e. an
    /// <c>IDENTITY</c> column on SQL Server. Supplying explicit values for those needs
    /// <c>SET IDENTITY_INSERT</c>. Keys that are not integers (GUID, string) are either literal
    /// values or defaulted, and need no special handling.
    /// </summary>
    private static bool HasIdentityKey(IEntityType type)
    {
        var key = type.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1)
        {
            return false;
        }

        var property = key.Properties[0];
        if (property.ValueGenerated != ValueGenerated.OnAdd)
        {
            return false;
        }

        return property.ClrType == typeof(int)
               || property.ClrType == typeof(long)
               || property.ClrType == typeof(short)
               || property.ClrType == typeof(byte);
    }

    private static string QualifyTable(IEntityType type)
        => $"[{type.GetSchema() ?? "dbo"}].[{type.GetTableName() ?? type.ClrType.Name}]";

    // The entity type is only known at runtime, so the generic set operations are reached through
    // a small reflection shim rather than a non-generic API that does not exist on DbContext.
    private static Task<List<object>> ReadRowsAsync(ApplicationDbContext context, Type clrType, CancellationToken ct)
    {
        var method = typeof(SqliteToSqlServerMigrator)
            .GetMethod(nameof(ReadRowsCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(clrType);

        return (Task<List<object>>)method.Invoke(null, new object[] { context, ct })!;
    }

    private static async Task<List<object>> ReadRowsCore<T>(ApplicationDbContext context, CancellationToken ct)
        where T : class
    {
        var rows = await context.Set<T>().AsNoTracking().ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    private static Task WriteRowsAsync(ApplicationDbContext context, Type clrType, List<object> rows, CancellationToken ct)
    {
        var method = typeof(SqliteToSqlServerMigrator)
            .GetMethod(nameof(WriteRowsCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(clrType);

        return (Task)method.Invoke(null, new object[] { context, rows, ct })!;
    }

    private static async Task WriteRowsCore<T>(ApplicationDbContext context, List<object> rows, CancellationToken ct)
        where T : class
    {
        context.Set<T>().AddRange(rows.Cast<T>());
        await context.SaveChangesAsync(ct);
        // Detach the batch so the next type starts from a clean slate.
        context.ChangeTracker.Clear();
    }
}
