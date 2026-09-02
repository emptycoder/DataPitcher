# DataPitcher Slice 16: Selection Execution, Preview and Counting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute a declared one-root selection safely on PostgreSQL and SQL Server to return bounded distinct stable keys, a read-only preview, and an exact cached distinct-key count.

**Architecture:** Core owns provider-free execution contracts, deterministic key aliases and cache identity, timeout/cancellation orchestration, and raw-SQL lexical safety rules. Each provider compiles the typed AST into its own quoted SQL and executes the result as a derived stable-key set; preview joins that set back to the declared root table, so joined tables only constrain roots. Providers use the existing catalog snapshots for native CLR types and column metadata, while access control remains at the Core service boundary.

**Tech Stack:** .NET SDK 10.0.400, C# latest, xUnit 2.9.3, Coverlet, SHA-256, Npgsql 10.0.3, Microsoft.Data.SqlClient 7.0.2, PostgreSQL 17, SQL Server 2022, Testcontainers 4.14.0.

---

## File Structure

- `src/DataPitcher.Core/Selection/SelectionQueryModels.cs` — extends generated SQL with an explicit raw-mode flag.
- `src/DataPitcher.Core/Selection/SelectionExecutionModels.cs` — immutable requests, results, key aliases, cache key, count cache, contracts, and exceptions.
- `src/DataPitcher.Core/Selection/SelectionExecutionService.cs` — permission gate, timeout wrapper, compilation, validation, and count-cache orchestration.
- `src/DataPitcher.Core/Selection/RawSqlSafetyValidator.cs` — lexer-based single-read-statement guard for the two SQL dialects.
- `src/DataPitcher.Core/Schema/ColumnDefinition.cs` — adds catalog-derived generated-column metadata with a backward-compatible default.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlCatalogReader.cs` — reads generated, text, and binary source metadata.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionSqlGenerator.cs` — aliases every selected root key with the Core contract.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionExecutor.cs` — validates, materializes keys, previews root rows, and counts PostgreSQL selections.
- `src/DataPitcher.Providers.SqlServer/SqlServerCatalogReader.cs` — reads computed/generated, text, and binary source metadata.
- `src/DataPitcher.Providers.SqlServer/SqlServerSelectionSqlGenerator.cs` — SQL Server AST compiler using the existing bracket quoter.
- `src/DataPitcher.Providers.SqlServer/SqlServerSelectionExecutor.cs` — validates, materializes keys, previews root rows, and counts SQL Server selections.
- `tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs` — Core models, permission, caching, timeout, cancellation, and raw lexical tests.
- `tests/DataPitcher.UnitTests/Selection/PostgreSqlSelectionSqlGeneratorTests.cs` — updates PostgreSQL key-projection expectations to the stable aliases.
- `tests/DataPitcher.UnitTests/Selection/SqlServerSelectionSqlGeneratorTests.cs` — SQL Server compiler, quoting, parameters, and one-root projection tests.
- `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj` — adds the existing SQL Server provider project reference for pure compiler tests.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlSelectionExecutionTests.cs` — PostgreSQL execution, preview, count, raw SQL, bounds, and cancellation tests.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerSelectionExecutionTests.cs` — corresponding SQL Server integration tests in the existing parallel-disabled collection.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs` — accepts an optional source command recorder for server-side-preview-limit assertions.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs` and `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs` — generated and binary metadata tests.

## Scope and Deferrals

This slice executes selections only against the source. It does not seal a transfer plan, compute dependency closure, persist saved selection versions, add API routes, or build frontend controls. A selection produces `StableKey` values for exactly one declared root; its joins, CTEs, subqueries, and `EXISTS` clauses are predicates over that root and never add a second transfer root. The dependency stage will receive those root keys later and remains responsible for outgoing dependency traversal.

The count cache is an in-process `SelectionCountCache` supplied to the Core service by composition. It is keyed by schema snapshot hash, normalized generated-query hash, typed parameter hash, and ordered stable-key definition, so a schema refresh, query/parameter change, or key-definition change cannot reuse a stale result. Distributed cache persistence and eviction policy are deliberately deferred; snapshot-hash identity is the correctness boundary in this slice.

Raw SQL is deliberately narrow: one `SELECT` or CTE-led `SELECT` for one declared root, and the complete root stable key must be projected as `__datapitcher_key_0`, `__datapitcher_key_1`, and so on. It may use joins, subqueries, CTEs, and `EXISTS`; result order is ignored for identity because outer queries always normalize to `DISTINCT` keys. Before provider execution, a trailing raw-SQL `ORDER BY` clause and statement terminator are removed lexically so providers may safely place the query in their required derived-table wrappers; quoted and commented `ORDER BY` and semicolon occurrences remain inert. Raw mode requires `Permissions.SelectionsRawSql`, already present in Core.

**Parsing is not the primary security boundary.** The source database principal must have read-only access to business schemas and write access only to the `__datapitcher` staging schema; it must have no business DDL or DML grant. The lexer below is defense in depth and gives useful validation errors, but no regular expression or application parser is trusted as the security boundary. PostgreSQL and SQL Server receive a provider-aware check for data-modifying tokens, statement separators, and SQL Server `GO` batch separators; the SQL is then executed only through parameterized commands under that restricted principal.

Selection execution does not reuse closure staging tables: closure staging needs source and target stores and durable generation metadata, whereas selection uses bounded derived key sets on the source connection. This avoids creating target objects for a source-only operation. Preview has an immutable server maximum of 200 rows, independent of any operator-authored `TOP`, `LIMIT`, or raw-query order clause. Text is capped at 256 characters and binary at 256 bytes in preview SQL only; transfer materialization is untouched and must read full native values later.

## Tasks

### Task 1: Define Core execution contracts, bounded service, and exact count cache

**Files:**
- Create: `src/DataPitcher.Core/Selection/SelectionExecutionModels.cs`, `src/DataPitcher.Core/Selection/SelectionExecutionService.cs`, `tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs`
- Modify: `src/DataPitcher.Core/Selection/SelectionQueryModels.cs`
- Test: `tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs`

- [ ] **Step 1: Write the failing Core tests for immutable results, aliases, raw permission, cache identity, cancellation, and each observed timeout.**

  ```csharp
  [Fact] public async Task CountAsync_CachesOnlyTheSameSchemaQueryParametersAndKeyDefinition()
  { var fake = new RecordingExecutor { Count = 2 }; var service = Service(fake); var request = AstRequest();
    Assert.Equal(2, await service.CountAsync(request, PermissionSet.Empty, CancellationToken.None));
    Assert.Equal(2, await service.CountAsync(request, PermissionSet.Empty, CancellationToken.None));
    Assert.Equal(1, fake.CountCalls);
    Assert.Equal(2, await service.CountAsync(request with { SchemaSnapshotHash = "changed" }, PermissionSet.Empty, CancellationToken.None));
    Assert.Equal(2, fake.CountCalls); }

  [Fact] public async Task ExecuteKeysAsync_WhenJoinFansOut_ReturnsOnlyTheDeclaredRootKeySet()
  { var fake = new RecordingExecutor { Keys = new SelectionKeySet(Orders, [Key(7)]) }; var result = await Service(fake).ExecuteKeysAsync(AstRequest(), PermissionSet.Empty, CancellationToken.None);
    Assert.Single(result.Keys, key => key == Key(7)); Assert.Equal("orders", result.RootTable.Name); }

  [Fact] public async Task ExecuteKeysAsync_WhenRawPermissionIsAbsent_ThrowsBeforeCallingTheProvider()
  { var fake = new RecordingExecutor(); var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(fake).ExecuteKeysAsync(RawRequest(), PermissionSet.Empty, CancellationToken.None));
    Assert.Equal("Missing permission: Selections.RawSql.", error.Message); Assert.Equal(0, fake.ValidationCalls); }

  [Theory] [InlineData("validation")] [InlineData("keys")] [InlineData("preview")] [InlineData("count")]
  public async Task Operations_WhenTheirBoundedOperationDoesNotComplete_ObserveTheNamedTimeout(string operation)
  { var fake = new BlockingExecutor(operation); var error = await Assert.ThrowsAsync<SelectionOperationTimeoutException>(() => InvokeAsync(Service(fake, TimeSpan.FromMilliseconds(20)), operation));
    Assert.Equal("Selection " + operation + " timed out.", error.Message); Assert.True(fake.Cancelled); }

  [Fact] public async Task PreviewAsync_UsesTheFixedServerLimitAndPropagatesCallerCancellation()
  { using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var fake = new RecordingExecutor();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(fake).PreviewAsync(AstRequest(), PermissionSet.Empty, cancelled.Token));
    Assert.Equal(SelectionExecutionLimits.PreviewRowLimit, fake.PreviewLimit); Assert.True(fake.ObservedCancellation); }
  ```

  The file declares `RecordingExecutor`, `BlockingExecutor`, `TestCompiler`, request builders, and `InvokeAsync`. It exercises every public constructor/property/member introduced below: defensive collection copies for raw parameters and preview maps, each `SelectionExecutionLimits.Validate` rejection, `SelectionKeyAliases.For` and `ForKey`, both cache outcomes, fingerprint changes for a typed parameter and a stable-key order change, `SelectionCountCacheKey.Create`, and `SelectionOperationTimeoutException`.

- [ ] **Step 2: Run the focused Core test and confirm the execution seam is absent.**

  Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~SelectionExecutionServiceTests"`

  Expected: compilation fails with CS0246 stating that `SelectionExecutionService`, `SelectionExecutionLimits`, and `ISelectionExecutor` could not be found. This is the intended red state; do not add provider execution code yet.

- [ ] **Step 3: Add the complete Core contracts and service.**

  ```csharp
  // SelectionQueryModels.cs: replace GeneratedSelectionSql with this compatible signature.
  public sealed class GeneratedSelectionSql
  { public GeneratedSelectionSql(string commandText, TableDefinition rootTable, UniqueConstraint rootStableKey, IEnumerable<SelectionSqlParameter> parameters, bool isRawSql = false)
    { CommandText = commandText; RootTable = rootTable; RootStableKey = rootStableKey; Parameters = Array.AsReadOnly(parameters.ToArray()); IsRawSql = isRawSql; }
    public string CommandText { get; } public TableDefinition RootTable { get; } public UniqueConstraint RootStableKey { get; }
    public IReadOnlyList<SelectionSqlParameter> Parameters { get; } public bool IsRawSql { get; } }

  // SelectionExecutionModels.cs
  using System.Collections.ObjectModel; using System.Security.Cryptography; using System.Text;
  using DataPitcher.Core.Identity; using DataPitcher.Core.Schema;
  namespace DataPitcher.Core.Selection;
  public enum RawSqlDialect { PostgreSql, SqlServer }
  public sealed class RawSqlSelection { public RawSqlSelection(TableDefinition rootTable, UniqueConstraint rootStableKey, string commandText, IEnumerable<SelectionSqlParameter> parameters) { RootTable = rootTable; RootStableKey = rootStableKey; CommandText = commandText; Parameters = Array.AsReadOnly(parameters.ToArray()); } public TableDefinition RootTable { get; } public UniqueConstraint RootStableKey { get; } public string CommandText { get; } public IReadOnlyList<SelectionSqlParameter> Parameters { get; } }
  public sealed record SelectionExecutionLimits(int MaximumResultSize, TimeSpan ValidationTimeout, TimeSpan KeyTimeout, TimeSpan PreviewTimeout, TimeSpan CountTimeout)
  { public const int PreviewRowLimit = 200; public const int PreviewTextLength = 256; public const int PreviewBinaryLength = 256; public static SelectionExecutionLimits Default { get; } = new(100000, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)); public void Validate() { if (MaximumResultSize < 1) throw new ArgumentOutOfRangeException(nameof(MaximumResultSize)); foreach (var value in new[] { ValidationTimeout, KeyTimeout, PreviewTimeout, CountTimeout }) if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value)); } }
  public sealed record SelectionExecutionRequest(string SchemaSnapshotHash, SelectionQuery? Query, RawSqlSelection? RawSql, SelectionExecutionLimits Limits)
  { public void Validate() { Limits.Validate(); if ((Query is null) == (RawSql is null)) throw new ArgumentException("Selection execution requires exactly one query mode."); } }
  public sealed class SelectionKeySet { public SelectionKeySet(TableDefinition rootTable, IEnumerable<StableKey> keys) { RootTable = rootTable; Keys = Array.AsReadOnly(keys.ToArray()); } public TableDefinition RootTable { get; } public IReadOnlyList<StableKey> Keys { get; } }
  public sealed record SelectionPreviewColumn(string Name, bool IsStableKey, bool IsForeignKey, bool IsGenerated);
  public sealed record SelectionPreviewCell(object? Value, bool IsTruncated);
  public sealed class SelectionPreviewRow { public SelectionPreviewRow(StableKey stableKey, IReadOnlyDictionary<string, SelectionPreviewCell> values) { StableKey = stableKey; Values = new ReadOnlyDictionary<string, SelectionPreviewCell>(new Dictionary<string, SelectionPreviewCell>(values, StringComparer.Ordinal)); } public StableKey StableKey { get; } public IReadOnlyDictionary<string, SelectionPreviewCell> Values { get; } }
  public sealed class SelectionPreview { public SelectionPreview(IEnumerable<SelectionPreviewColumn> columns, IEnumerable<SelectionPreviewRow> rows) { Columns = Array.AsReadOnly(columns.ToArray()); Rows = Array.AsReadOnly(rows.ToArray()); } public IReadOnlyList<SelectionPreviewColumn> Columns { get; } public IReadOnlyList<SelectionPreviewRow> Rows { get; } }
  public static class SelectionKeyAliases { public static string For(int ordinal) => ordinal < 0 ? throw new ArgumentOutOfRangeException(nameof(ordinal)) : "__datapitcher_key_" + ordinal; public static IReadOnlyList<string> ForKey(UniqueConstraint key) => Array.AsReadOnly(Enumerable.Range(0, key.Columns.Count).Select(For).ToArray()); }
  public sealed record SelectionCountCacheKey(string SchemaSnapshotHash, string NormalizedQueryHash, string ParameterHash, string StableKeyDefinition)
  { public static SelectionCountCacheKey Create(string schemaHash, GeneratedSelectionSql sql) => new(schemaHash, Hash(sql.CommandText), Hash(string.Join("\u001f", sql.Parameters.Select(p => p.Name + "\u001e" + p.ClrType.FullName + "\u001e" + System.Text.Json.JsonSerializer.Serialize(p.Value, p.ClrType))), string.Join("\u001f", sql.RootStableKey.Columns)); private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))); }
  public sealed class SelectionCountCache { private readonly Dictionary<SelectionCountCacheKey, long> values = []; public bool TryGet(SelectionCountCacheKey key, out long count) => values.TryGetValue(key, out count); public void Set(SelectionCountCacheKey key, long count) => values[key] = count; }
  public sealed class SelectionResultLimitExceededException(int maximum) : InvalidOperationException("Selection result exceeds the maximum of " + maximum + " stable keys.");
  public sealed class SelectionOperationTimeoutException(string operation) : TimeoutException("Selection " + operation + " timed out.");
  public sealed class RawSqlValidationException(string message) : InvalidOperationException(message);
  public interface ISelectionSqlCompiler { GeneratedSelectionSql Compile(SelectionQuery query); }
  public interface ISelectionExecutor { Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken); Task<SelectionKeySet> ReadKeysAsync(GeneratedSelectionSql selection, int maximumResultSize, CancellationToken cancellationToken); Task<SelectionPreview> PreviewAsync(GeneratedSelectionSql selection, int rowLimit, int textLimit, int binaryLimit, CancellationToken cancellationToken); Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken); }
  ```

  ```csharp
  // SelectionExecutionService.cs
  using DataPitcher.Core.Authorization;
  namespace DataPitcher.Core.Selection;
  public sealed class SelectionExecutionService(ISelectionSqlCompiler compiler, ISelectionExecutor executor, SelectionCountCache counts)
  {
      public async Task<SelectionKeySet> ExecuteKeysAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken) { var sql = await PrepareAsync(request, permissions, cancellationToken); return await BoundedAsync("keys", request.Limits.KeyTimeout, token => executor.ReadKeysAsync(sql, request.Limits.MaximumResultSize, token), cancellationToken); }
      public async Task<SelectionPreview> PreviewAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken) { var sql = await PrepareAsync(request, permissions, cancellationToken); return await BoundedAsync("preview", request.Limits.PreviewTimeout, token => executor.PreviewAsync(sql, SelectionExecutionLimits.PreviewRowLimit, SelectionExecutionLimits.PreviewTextLength, SelectionExecutionLimits.PreviewBinaryLength, token), cancellationToken); }
      public async Task<long> CountAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken) { var sql = await PrepareAsync(request, permissions, cancellationToken); var key = SelectionCountCacheKey.Create(request.SchemaSnapshotHash, sql); if (counts.TryGet(key, out var cached)) return cached; var count = await BoundedAsync("count", request.Limits.CountTimeout, token => executor.CountAsync(sql, token), cancellationToken); counts.Set(key, count); return count; }
      private async Task<GeneratedSelectionSql> PrepareAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken) { request.Validate(); var sql = request.RawSql is { } raw ? permissions.Contains(Permissions.SelectionsRawSql) ? new GeneratedSelectionSql(raw.CommandText, raw.RootTable, raw.RootStableKey, raw.Parameters, true) : throw new UnauthorizedAccessException("Missing permission: Selections.RawSql.") : compiler.Compile(request.Query!); await BoundedAsync("validation", request.Limits.ValidationTimeout, async token => { await executor.ValidateAsync(sql, token); return true; }, cancellationToken); return sql; }
      private static async Task<T> BoundedAsync<T>(string operation, TimeSpan timeout, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) { using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout); try { return await action(linked.Token); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new SelectionOperationTimeoutException(operation); } }
  }
  ```

  The service validates every operation independently, deliberately does not return a partial key set at the maximum, and passes the linked token to every provider call. The cache hashes normalized compiler output for AST mode; raw mode hashes exact submitted SQL plus typed parameters because arbitrary SQL has no safe AST normalization.

- [ ] **Step 4: Run the focused Core tests and confirm the contracts, cache, timeout observations, and permission gate pass.**

  Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~SelectionExecutionServiceTests"`

  Expected: all focused tests pass. The four blocking cases each observe their linked token cancellation and report `Selection validation timed out.`, `Selection keys timed out.`, `Selection preview timed out.`, or `Selection count timed out.`; caller cancellation remains `OperationCanceledException` rather than a timeout.

- [ ] **Step 5: Commit the Core selection execution seam.**

  Run: `git add src/DataPitcher.Core/Selection/SelectionQueryModels.cs src/DataPitcher.Core/Selection/SelectionExecutionModels.cs src/DataPitcher.Core/Selection/SelectionExecutionService.cs tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs && git commit -m "feat: add selection execution contracts"`

### Task 2: Add provider-aware raw SQL lexical validation

**Files:**
- Create: `src/DataPitcher.Core/Selection/RawSqlSafetyValidator.cs`
- Modify: `tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs`
- Test: `tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs`

- [ ] **Step 1: Write failing lexical-guard tests for accepted CTEs and rejected statement forms.**

  ```csharp
  [Theory]
  [InlineData(RawSqlDialect.PostgreSql, "WITH roots AS (SELECT 7 AS __datapitcher_key_0) SELECT __datapitcher_key_0 FROM roots")]
  [InlineData(RawSqlDialect.SqlServer, "WITH roots AS (SELECT 7 AS __datapitcher_key_0) SELECT __datapitcher_key_0 FROM roots")]
  public void RawSqlSafetyValidator_AcceptsOneReadOnlySelect(RawSqlDialect dialect, string sql) => RawSqlSafetyValidator.Validate(dialect, sql);
  [Theory]
  [InlineData(RawSqlDialect.PostgreSql, "DELETE FROM orders", "Raw SQL must start with SELECT or WITH.")]
  [InlineData(RawSqlDialect.PostgreSql, "SELECT 1; SELECT 2", "Raw SQL may contain only one statement.")]
  [InlineData(RawSqlDialect.PostgreSql, "WITH x AS (DELETE FROM orders RETURNING order_id) SELECT order_id AS __datapitcher_key_0 FROM x", "Raw SQL contains a data-modifying token: DELETE.")]
  [InlineData(RawSqlDialect.SqlServer, "SELECT 1\nGO\nSELECT 2", "SQL Server batch separators are not allowed.")]
  public void RawSqlSafetyValidator_RejectsUnsafeSql(RawSqlDialect dialect, string sql, string message)
  { Assert.Equal(message, Assert.Throws<RawSqlValidationException>(() => RawSqlSafetyValidator.Validate(dialect, sql)).Message); }
  [Fact] public void RawSqlSafetyValidator_IgnoresKeywordsInsideQuotedValuesAndComments()
  { RawSqlSafetyValidator.Validate(RawSqlDialect.PostgreSql, "SELECT 'DELETE; INSERT' AS __datapitcher_key_0 /* UPDATE */"); }
  ```

- [ ] **Step 2: Run the focused guard tests and confirm the lexer is absent.**

  Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~RawSqlSafetyValidator"`

  Expected: compilation fails with CS0103 stating that `RawSqlSafetyValidator` does not exist in the current context.

- [ ] **Step 3: Implement the complete lexer-based guard without regular-expression parsing.**

  ```csharp
  namespace DataPitcher.Core.Selection;
  public static class RawSqlSafetyValidator
  {
      private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase) { "ALTER", "ANALYZE", "CALL", "COMMIT", "COPY", "CREATE", "DECLARE", "DELETE", "DO", "DROP", "EXEC", "EXECUTE", "GRANT", "INSERT", "LOCK", "MERGE", "REVOKE", "ROLLBACK", "SET", "TRUNCATE", "UPDATE", "USE", "VACUUM" };
      public static void Validate(RawSqlDialect dialect, string sql)
      {
          var tokens = Tokens(sql); if (dialect == RawSqlDialect.SqlServer && tokens.Any(token => EqualsToken(token, "GO"))) throw new RawSqlValidationException("SQL Server batch separators are not allowed.");
          if (tokens.Count == 0 || (!EqualsToken(tokens[0], "SELECT") && !EqualsToken(tokens[0], "WITH"))) throw new RawSqlValidationException("Raw SQL must start with SELECT or WITH.");
          var separators = tokens.Select((token, index) => (token, index)).Where(pair => pair.token == ";").ToArray(); if (separators.Length > 1 || separators.Length == 1 && separators[0].index != tokens.Count - 1) throw new RawSqlValidationException("Raw SQL may contain only one statement.");
          foreach (var token in tokens.Where(token => token != ";")) { if (Forbidden.Contains(token)) throw new RawSqlValidationException("Raw SQL contains a data-modifying token: " + token.ToUpperInvariant() + "."); if (EqualsToken(token, "INTO")) throw new RawSqlValidationException("Raw SQL contains a data-modifying token: INTO."); }
      }
      private static bool IsGoLine(string line) { var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); return parts.Length is 1 or 2 && EqualsToken(parts[0], "GO") && (parts.Length == 1 || int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)); }
      private static bool EqualsToken(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
      private static List<string> Tokens(string sql)
      {
          var result = new List<string>(); for (var index = 0; index < sql.Length;) { var value = sql[index]; if (char.IsWhiteSpace(value)) { index++; continue; } if (value == '-' && index + 1 < sql.Length && sql[index + 1] == '-') { index = SkipLine(sql, index + 2); continue; } if (value == '/' && index + 1 < sql.Length && sql[index + 1] == '*') { index = SkipBlock(sql, index + 2); continue; } if (value == '\'' || value == '"') { index = SkipQuoted(sql, index, value); continue; } if (value == '[') { index = SkipBracket(sql, index + 1); continue; } if (value == ';') { result.Add(";"); index++; continue; } if (char.IsLetter(value) || value == '_') { var start = index++; while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_' || sql[index] == '$')) index++; result.Add(sql[start..index]); continue; } index++; } return result;
      }
      private static int SkipLine(string sql, int index) { while (index < sql.Length && sql[index] != '\n') index++; return index; }
      private static int SkipBlock(string sql, int index) { var depth = 1; while (index + 1 < sql.Length && depth > 0) { if (sql[index] == '/' && sql[index + 1] == '*') { depth++; index += 2; } else if (sql[index] == '*' && sql[index + 1] == '/') { depth--; index += 2; } else index++; } if (depth != 0) throw new RawSqlValidationException("Raw SQL has an unterminated block comment."); return index; }
      private static int SkipQuoted(string sql, int index, char quote) { index++; while (index < sql.Length) { if (sql[index] == quote) { if (index + 1 < sql.Length && sql[index + 1] == quote) { index += 2; continue; } return index + 1; } index++; } throw new RawSqlValidationException("Raw SQL has an unterminated quoted value."); }
      private static int SkipBracket(string sql, int index) { while (index < sql.Length) { if (sql[index] == ']') { if (index + 1 < sql.Length && sql[index + 1] == ']') { index += 2; continue; } return index + 1; } index++; } throw new RawSqlValidationException("Raw SQL has an unterminated bracket identifier."); }
  }
  ```

  This scanner is intentionally a lexical guard, not a SQL grammar or authorization control. It consumes quoted strings, quoted identifiers, bracket identifiers, line comments, and nested block comments before searching tokens, which prevents the usual comment/string bypass without treating a regular expression as a parser.

- [ ] **Step 4: Run the raw guard tests and confirm the permitted and forbidden forms pass.**

  Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~RawSqlSafetyValidator"`

  Expected: the CTE cases pass, while DML, a data-modifying CTE, multiple statements, and `GO` return the exact expected validation messages. Quoted and commented keywords remain inert.

- [ ] **Step 5: Commit the raw SQL guard.**

  Run: `git add src/DataPitcher.Core/Selection/RawSqlSafetyValidator.cs tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs && git commit -m "feat: validate raw selection sql"`

### Task 3: Complete source metadata and both stable-key SQL compilers

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerSelectionSqlGenerator.cs`, `tests/DataPitcher.UnitTests/Selection/SqlServerSelectionSqlGeneratorTests.cs`
- Modify: `src/DataPitcher.Core/Schema/ColumnDefinition.cs`, `src/DataPitcher.Providers.PostgreSql/PostgreSqlCatalogReader.cs`, `src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionSqlGenerator.cs`, `src/DataPitcher.Providers.SqlServer/SqlServerCatalogReader.cs`, `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj`, `tests/DataPitcher.UnitTests/Selection/PostgreSqlSelectionSqlGeneratorTests.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs`
- Test: `tests/DataPitcher.UnitTests/Selection/SqlServerSelectionSqlGeneratorTests.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs`

- [ ] **Step 1: Write failing compiler and catalog tests for aliases, joins, generated fields, and binary fields.**

  ```csharp
  [Fact] public void Compile_ProjectsOnlyAliasedDistinctRootKeys_WhenTheQueryJoinsOrderLines()
  { var query = OrdersJoinedToLines(); var sql = new SqlServerSelectionSqlGenerator().Compile(query);
    Assert.StartsWith("SELECT DISTINCT [o].[order_id] AS [__datapitcher_key_0]", sql.CommandText, StringComparison.Ordinal);
    Assert.Contains("INNER JOIN [dbo].[order_lines] AS [l]", sql.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("[l].[line_id] AS [__datapitcher_key_", sql.CommandText, StringComparison.Ordinal); }
  [Fact] public async Task ReadAsync_ReportsGeneratedAndBinaryColumns()
  { await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteAsync("CREATE TABLE dbo.preview_metadata (id int PRIMARY KEY, payload varbinary(max) NOT NULL, calculated AS id + 1)");
    var table = (await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None)).Table("preview_metadata");
    Assert.Equal(typeof(byte[]), table.Column("payload").ClrType); Assert.True(table.Column("calculated").IsGenerated); }
  ```

  Add the matching PostgreSQL catalog test using `payload bytea NOT NULL, calculated integer GENERATED ALWAYS AS (id + 1) STORED`. Update the existing PostgreSQL generator test to require `"r"."Id" AS "__datapitcher_key_0"`; retain its parameter and identifier-escape assertions. Exercise SQL Server comparison, set, text, temporal, `EXISTS`, forward/reverse foreign-key, manual-join, composite-key, and culture-invariant branches just as the established PostgreSQL test suite does.

- [ ] **Step 2: Run the focused compiler/catalog tests and confirm the SQL Server compiler and metadata flag are absent.**

  Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~SqlServerSelectionSqlGeneratorTests" && ./scripts/test-postgres.sh --filter "FullyQualifiedName~PostgreSqlCatalogReaderTests" && ./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerCatalogReaderTests"`

  Expected: the unit build fails with CS0246 for `SqlServerSelectionSqlGenerator`; catalog tests fail to compile because `ColumnDefinition` has no `IsGenerated` property. Do not change execution code in this task.

- [ ] **Step 3: Implement catalog metadata and closed identifier-safe compilers.**

  ```csharp
  // ColumnDefinition.cs
  public sealed record ColumnDefinition(string Name, Type ClrType, bool IsNullable, bool IsGenerated = false);

  // PostgreSqlCatalogReader.ColumnsSql and read loop
  // Select a.attgenerated <> '' after NOT a.attnotnull; pass reader.GetBoolean(4) to ColumnDefinition.
  // Extend Map: "bytea" => typeof(byte[]), and compare table/column names with StringComparer.Ordinal.
  // SqlServerCatalogReader.ColumnsSql
  // SELECT t.name,c.name,ty.name,c.max_length,c.is_nullable,CAST(CASE WHEN cc.is_computed=1 OR c.generated_always_type<>0 THEN 1 ELSE 0 END AS bit)
  // FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id
  // JOIN sys.types ty ON ty.user_type_id=c.user_type_id LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
  // WHERE s.name=@schema ORDER BY t.name,c.column_id
  // Pass the sixth field to ColumnDefinition and extend Map: "varbinary" => typeof(byte[]).
  ```

  Change both compiler projections to emit, in stable-key declaration order, `rootAlias.quotedColumn AS quoted(SelectionKeyAliases.For(index))`. PostgreSQL retains its existing fixed-token writer and `||` `LIKE` form. Add `SqlServerSelectionSqlGenerator : ISelectionSqlCompiler` with the same AST cases, `SqlServerIdentifier.Quote` as its only identifier route, `+` for its escaped `LIKE` forms, and generated `@p0` parameter records. Its root projection must be equivalent to:

  ```sql
  SELECT DISTINCT [o].[order_id] AS [__datapitcher_key_0]
  FROM [dbo].[orders] AS [o]
  INNER JOIN [dbo].[order_lines] AS [l] ON [o].[order_id] = [l].[order_id]
  ```

  Add the existing SQL Server provider project reference to `DataPitcher.UnitTests.csproj`; no package is added. All identifier/alias/column comparisons in the new and modified paths use ordinal comparison. The generated alias is a transport contract, not a business-column name, and lets raw SQL validation compare database-reported columns to the exact stable-key shape.

- [ ] **Step 4: Run compiler and catalog tests and confirm both dialects report the same one-root contract.**

  Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~PostgreSqlSelectionSqlGeneratorTests|FullyQualifiedName~SqlServerSelectionSqlGeneratorTests" && ./scripts/test-postgres.sh --filter "FullyQualifiedName~PostgreSqlCatalogReaderTests" && ./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerCatalogReaderTests"`

  Expected: all focused tests pass. Both compilers project only aliased root stable keys despite a join, and both catalog readers identify generated and binary columns with their declared CLR types.

- [ ] **Step 5: Commit source metadata and compiler parity.**

  Run: `git add src/DataPitcher.Core/Schema/ColumnDefinition.cs src/DataPitcher.Providers.PostgreSql/PostgreSqlCatalogReader.cs src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionSqlGenerator.cs src/DataPitcher.Providers.SqlServer/SqlServerCatalogReader.cs src/DataPitcher.Providers.SqlServer/SqlServerSelectionSqlGenerator.cs tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj tests/DataPitcher.UnitTests/Selection/PostgreSqlSelectionSqlGeneratorTests.cs tests/DataPitcher.UnitTests/Selection/SqlServerSelectionSqlGeneratorTests.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs && git commit -m "feat: compile selection keys for both providers"`

### Task 4: Execute, preview, count, and validate selections on PostgreSQL

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionExecutor.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlSelectionExecutionTests.cs`
- Modify: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs`
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlSelectionExecutionTests.cs`

- [ ] **Step 1: Write failing PostgreSQL integration tests using the separate source and target containers.**

  ```csharp
  [Fact] public async Task ReadKeysAsync_WhenOneOrderHasFiveJoinedLines_ReturnsOneOrderKeyAndNoSecondRoot()
  { await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteAsync("INSERT INTO customers VALUES (1,'c'); INSERT INTO orders VALUES (10,1); INSERT INTO order_lines VALUES (1,10),(2,10),(3,10),(4,10),(5,10);");
    var executor = Executor(scope); var keys = await executor.ReadKeysAsync(Compile(OrdersWithLines(scope)), 100, CancellationToken.None);
    Assert.Equal("orders", keys.RootTable.Name); Assert.Single(keys.Keys, key => key == new StableKey([new("order_id", 10)])); }
  [Fact] public async Task CountAsync_WhenJoinFansOutFiveLines_CountsOneDistinctOrder()
  { await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteAsync("INSERT INTO customers VALUES (1,'c'); INSERT INTO orders VALUES (10,1); INSERT INTO order_lines VALUES (1,10),(2,10),(3,10),(4,10),(5,10);");
    Assert.Equal(1, await Executor(scope).CountAsync(Compile(OrdersWithLines(scope)), CancellationToken.None)); }
  [Fact] public async Task PreviewAsync_UsesServerBoundAndTruncatesOnlyPreviewValues()
  { await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteAsync("CREATE TABLE preview_orders (id integer PRIMARY KEY, customer_id integer REFERENCES customers(customer_id), note text NOT NULL, payload bytea NOT NULL, generated integer GENERATED ALWAYS AS (id + 1) STORED)");
    await scope.ExecuteAsync("INSERT INTO customers VALUES (1,'c'); INSERT INTO preview_orders(id,customer_id,note,payload) SELECT value,1,repeat('x',300),decode(repeat('ab',300),'hex') FROM generate_series(1,201) value;");
    var preview = await Executor(scope).PreviewAsync(RawPreview(scope), 200, 256, 256, CancellationToken.None);
    Assert.Equal(200, preview.Rows.Count); Assert.Single(preview.Columns, column => column.Name == "id" && column.IsStableKey); Assert.Single(preview.Columns, column => column.Name == "customer_id" && column.IsForeignKey); Assert.Single(preview.Columns, column => column.Name == "generated" && column.IsGenerated); Assert.True(preview.Rows[0].Values["note"].IsTruncated); Assert.Equal(256, ((string)preview.Rows[0].Values["note"].Value!).Length); Assert.True(preview.Rows[0].Values["payload"].IsTruncated); Assert.Equal(256, ((byte[])preview.Rows[0].Values["payload"].Value!).Length); }
  ```

  Tests validate a raw CTE selecting every expected alias; reject a raw result missing `__datapitcher_key_0`; prove a 201st key causes `SelectionResultLimitExceededException` instead of a partial transfer; pre-cancel `ValidateAsync`, `ReadKeysAsync`, `PreviewAsync`, and `CountAsync` to prove the provider passes cancellation to each Npgsql operation; and verify the source recorder observed `LIMIT @previewLimit` rather than an operator-provided limit. Assert the target row count stays unchanged throughout, proving source-only reads.

- [ ] **Step 2: Run the PostgreSQL integration tests and confirm the executor is absent.**

  Run: `./scripts/test-postgres.sh --filter "FullyQualifiedName~PostgreSqlSelectionExecutionTests"`

  Expected: compilation fails with CS0246 stating that `PostgreSqlSelectionExecutor` could not be found.

- [ ] **Step 3: Implement the PostgreSQL executor with parameterized derived key sets.**

  ```csharp
  public sealed class PostgreSqlSelectionExecutor(NpgsqlDataSource source, PostgreSqlSchemaSnapshot schema) : ISelectionExecutor
  {
      public async Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) { if (selection.IsRawSql) RawSqlSafetyValidator.Validate(RawSqlDialect.PostgreSql, selection.CommandText); await using var command = Command("/* DataPitcher.Selection.Validate */ SELECT * FROM (" + selection.CommandText + ") AS selection LIMIT 1", selection); await using var reader = await command.ExecuteReaderAsync(cancellationToken); RequireAliases(reader, selection.RootStableKey); }
      public async Task<SelectionKeySet> ReadKeysAsync(GeneratedSelectionSql selection, int maximumResultSize, CancellationToken cancellationToken) { var aliases = SelectionKeyAliases.ForKey(selection.RootStableKey); var columns = string.Join(", ", aliases.Select(PostgreSqlIdentifier.Quote)); await using var command = Command("/* DataPitcher.Selection.Keys */ SELECT DISTINCT " + columns + " FROM (" + selection.CommandText + ") AS selection ORDER BY " + columns + " LIMIT @take", selection); command.Parameters.AddWithValue("take", checked(maximumResultSize + 1)); var keys = await ReadKeysAsync(command, selection.RootStableKey, cancellationToken); if (keys.Count > maximumResultSize) throw new SelectionResultLimitExceededException(maximumResultSize); return new SelectionKeySet(selection.RootTable, keys); }
      public async Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) { var aliases = string.Join(", ", SelectionKeyAliases.ForKey(selection.RootStableKey).Select(PostgreSqlIdentifier.Quote)); await using var command = Command("/* DataPitcher.Selection.Count */ SELECT count(*) FROM (SELECT DISTINCT " + aliases + " FROM (" + selection.CommandText + ") AS selection) AS keys", selection); return (long)(await command.ExecuteScalarAsync(cancellationToken))!; }
  }
  ```

  Private `Command`, key-reader, preview-reader, and alias-validator methods copy every `SelectionSqlParameter` by name and value, build quoted root joins, and compare `NpgsqlDataReader.GetName` ordinally. `PreviewAsync` builds a `selection` CTE from `selection.CommandText`, then `keys AS (SELECT DISTINCT aliases FROM selection ORDER BY aliases LIMIT @previewLimit)`, and joins each declaration-ordered root stable column to its matching `__datapitcher_key_N` column. Its select list contains every root column and an adjacent `__datapitcher_truncated_N` Boolean: use `left(root.column,@textLimit)` with `length` for strings, `substring(root.column FROM 1 FOR @binaryLimit)` with `octet_length` for `bytea`, and an untruncated value/false flag for other types. Build preview columns from the catalog snapshot: stable key membership, foreign-key child columns, and `ColumnDefinition.IsGenerated`. Return only read-only Core result collections.

  The fixture overload takes `PostgreSqlCommandRecorder? sourceRecorder` and wires it into the source data source exactly as the current target overload does. No table is created, truncated, or written by this executor. `ORDER BY` provides reproducible display/key output only; the outer `DISTINCT` projected stable keys, not ordering, define row identity.

- [ ] **Step 4: Run the PostgreSQL integration tests and confirm distinct keys, preview safety, raw validation, bounds, and cancellation pass.**

  Run: `./scripts/test-postgres.sh --filter "FullyQualifiedName~PostgreSqlSelectionExecutionTests"`

  Expected: all focused tests pass. Five joined lines count and materialize as one `orders` stable key, the preview remains 200 rows server-side with 256-character/byte preview-only truncation, raw CTE aliases work, and each pre-cancelled database operation observes cancellation.

- [ ] **Step 5: Commit PostgreSQL selection execution.**

  Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionExecutor.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlSelectionExecutionTests.cs && git commit -m "feat: execute postgres selections"`

### Task 5: Execute, preview, count, and validate selections on SQL Server

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerSelectionExecutor.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerSelectionExecutionTests.cs`
- Modify: `src/DataPitcher.Core/Selection/RawSqlSafetyValidator.cs`, `tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs`
- Test: `tests/DataPitcher.UnitTests/Selection/SelectionExecutionServiceTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerSelectionExecutionTests.cs`

- [ ] **Step 1: Write failing SQL Server integration tests matching the PostgreSQL observable contract.**

  ```csharp
  [Collection("SqlServer closure")]
  public sealed class SqlServerSelectionExecutionTests(SqlServerClosureFixture fixture)
  {
      [Fact] public async Task CountAsync_WhenOneOrderJoinsFiveLines_CountsOneDistinctRootKey()
      { await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteAsync("INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.orders VALUES (10,1); INSERT dbo.order_lines VALUES (1,10),(2,10),(3,10),(4,10),(5,10);");
        var executor = Executor(scope); var selection = Compile(OrdersWithLines(scope)); var keys = await executor.ReadKeysAsync(selection, 100, CancellationToken.None);
        Assert.Single(keys.Keys, key => key == new StableKey([new("order_id", 10)])); Assert.Equal(1, await executor.CountAsync(selection, CancellationToken.None)); }
      [Fact] public async Task PreviewAsync_LabelsProtectedColumnsAndTruncatesWithoutChangingTheSource()
      { await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteAsync("CREATE TABLE dbo.preview_orders (id int PRIMARY KEY, customer_id int REFERENCES dbo.customers(customer_id), note nvarchar(max) NOT NULL, payload varbinary(max) NOT NULL, calculated AS id + 1); INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.preview_orders VALUES (1,1,REPLICATE(N'x',300),CONVERT(varbinary(max),REPLICATE('a',300)));");
        var preview = await Executor(scope).PreviewAsync(RawPreview(scope), 200, 256, 256, CancellationToken.None);
        Assert.Single(preview.Rows); Assert.Single(preview.Columns, column => column.Name == "id" && column.IsStableKey); Assert.Single(preview.Columns, column => column.Name == "customer_id" && column.IsForeignKey); Assert.Single(preview.Columns, column => column.Name == "calculated" && column.IsGenerated); Assert.True(preview.Rows[0].Values["note"].IsTruncated); Assert.True(preview.Rows[0].Values["payload"].IsTruncated); Assert.Equal(1, await scope.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.preview_orders")); }
  }
  ```

  Add complete tests for default 200-row enforcement with 201 source rows, a result-size overflow, valid raw CTE aliases, missing aliases, raw `GO`, DML and multiple-statement rejections, and pre-cancelled validation/key/preview/count commands. Add a raw SQL test with a trailing `ORDER BY` that proves validation, key materialization, preview, and counting work through a SQL Server derived-table wrapper; separately prove quoted and commented `ORDER BY` occurrences are not removed. Keep the existing source and target containers separate and assert the target remains empty. This class must retain `[Collection("SqlServer closure")]`; do not introduce a second fixture or enable parallelism on the arm64 translation lane.

- [ ] **Step 2: Run the SQL Server integration tests and confirm the executor is absent.**

  Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerSelectionExecutionTests"`

  Expected: compilation fails with CS0246 stating that `SqlServerSelectionExecutor` could not be found.

- [ ] **Step 3: Implement SQL Server derived-key execution with the same Core results.**

  ```csharp
  public sealed class SqlServerSelectionExecutor(string sourceConnectionString, SqlServerSchemaSnapshot schema) : ISelectionExecutor
  {
      public async Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) { if (selection.IsRawSql) RawSqlSafetyValidator.Validate(RawSqlDialect.SqlServer, selection.CommandText); await using var connection = await OpenAsync(cancellationToken); await using var command = Command(connection, "/* DataPitcher.Selection.Validate */ SELECT TOP (1) * FROM (" + CommandText(selection) + ") AS selection", selection); await using var rows = await command.ExecuteReaderAsync(cancellationToken); RequireAliases(rows, selection.RootStableKey); }
      public async Task<SelectionKeySet> ReadKeysAsync(GeneratedSelectionSql selection, int maximumResultSize, CancellationToken cancellationToken) { var aliases = SelectionKeyAliases.ForKey(selection.RootStableKey); var columns = string.Join(", ", aliases.Select(SqlServerIdentifier.Quote)); await using var connection = await OpenAsync(cancellationToken); await using var command = Command(connection, "/* DataPitcher.Selection.Keys */ SELECT DISTINCT TOP (@take) " + columns + " FROM (" + CommandText(selection) + ") AS selection ORDER BY " + columns, selection); command.Parameters.AddWithValue("@take", checked(maximumResultSize + 1)); var keys = await ReadKeysAsync(command, selection.RootStableKey, cancellationToken); if (keys.Count > maximumResultSize) throw new SelectionResultLimitExceededException(maximumResultSize); return new SelectionKeySet(selection.RootTable, keys); }
      public async Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) { var aliases = string.Join(", ", SelectionKeyAliases.ForKey(selection.RootStableKey).Select(SqlServerIdentifier.Quote)); await using var connection = await OpenAsync(cancellationToken); await using var command = Command(connection, "/* DataPitcher.Selection.Count */ SELECT COUNT_BIG(*) FROM (SELECT DISTINCT " + aliases + " FROM (" + CommandText(selection) + ") AS selection) AS keys", selection); return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture); }
      private static string CommandText(GeneratedSelectionSql selection) => selection.IsRawSql ? RawSqlSafetyValidator.RemoveTrailingOrderBy(selection.CommandText) : selection.CommandText;
      private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) { var connection = new SqlConnection(sourceConnectionString); await connection.OpenAsync(cancellationToken); return connection; }
  }
  ```

  `PreviewAsync` uses a `selection` CTE from the raw SQL after `RawSqlSafetyValidator` lexically removes its trailing `ORDER BY` and statement terminator, then `keys AS (SELECT DISTINCT TOP (@previewLimit) aliases FROM selection ORDER BY aliases)`, joins every declaration-ordered root stable column to its `__datapitcher_key_N` key column, and orders by those root stable columns. Apply the same raw-only trailing-order and terminator removal before every SQL Server derived-table wrapper, using token positions rather than a regular expression so quoted or commented text cannot be confused with a clause. For `nvarchar`, select `CASE WHEN LEN(root.column)>@textLimit THEN LEFT(root.column,@textLimit) ELSE root.column END` and its flag; for `varbinary`, use `DATALENGTH` and `SUBSTRING`; all other columns retain native values and a false flag. Private `Command`, key-reader, preview-reader, and `RequireAliases` methods add every typed selection parameter and compare reader names ordinally; all `SqlConnection`, `SqlCommand`, reader execution, and row reads receive the supplied token. Do not use `MERGE`, staging tables, `IN` lists, or an operator SQL row limit as the preview guard.

- [ ] **Step 4: Run the SQL Server integration test once and then the complete existing lane.**

  Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerSelectionExecutionTests" && ./scripts/test-sqlserver.sh`

  Expected: the focused tests pass first; the complete parallel-disabled SQL Server lane then passes with no analyzer warnings. The fan-out count is one root order, preview never exceeds 200 server-side, raw aliases are mandatory, and every operation observes cancellation. Expect this lane to take approximately four minutes and thirty seconds under arm64 binary translation.

- [ ] **Step 5: Commit SQL Server selection execution.**

  Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerSelectionExecutor.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerSelectionExecutionTests.cs && git commit -m "feat: execute sqlserver selections"`

## Self-Review

Coverage: every public Core contract, cache member, service operation, key-alias member, raw guard branch, generated SQL compiler, catalog metadata path, and provider executor operation has a test in its introducing task. The Core fake observes all four timeout paths actually cancel, while both live-provider suites assert cancellation is passed through validation, key execution, preview, and counting. PostgreSQL and SQL Server tests use independent source/target containers; the SQL Server tests remain in the existing disabled-parallelization collection. The full `./scripts/test-all.sh` merged 100% line, branch, and method gate is the final cross-suite check after Task 5.

Deferrals: frontend workbench behavior, API endpoints, saved-selection persistence, long-lived/distributed count-cache eviction, closure/staging integration, plan sealing, transfer payload reads, and cross-provider transfers remain outside this slice. No preview value is editable, no preview truncation affects transfer data, no join becomes a root transfer table, and no raw SQL parser is treated as an authorization boundary.

Consistency: checked task order and type/method names. Task 1 defines `GeneratedSelectionSql.IsRawSql`, `SelectionExecutionRequest`, `SelectionExecutionLimits`, `SelectionKeyAliases`, `SelectionCountCacheKey`, `ISelectionSqlCompiler`, `ISelectionExecutor`, and all result/exception types used later. Task 2 defines `RawSqlSafetyValidator` before both providers call it; Task 3 defines both compiler implementations and `ColumnDefinition.IsGenerated` before provider preview code and tests rely on them. All aliases are `__datapitcher_key_N`, all string comparisons are ordinal or ordinal-ignore-case for SQL keywords, and the test examples avoid C# keyword patterns, target-typed `new()` in `params`, assigned `Assert.NotNull`, and analyzer-hostile LINQ assertion forms.
