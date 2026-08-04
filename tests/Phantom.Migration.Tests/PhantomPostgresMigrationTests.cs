using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Phantom.Migration.Tests;

/// <summary>
/// Regression coverage for the P4 Stage A phantom.db -> PostgreSQL store-relocation
/// (<c>scripts/phantom-migrate-jellyfindb-to-postgres.sh --source phantom</c>): the
/// data migration that moves the per-color SQLite <c>phantom.db</c> into the
/// <c>phantom_&lt;role&gt;</c> logical Postgres DB through the P3 methodology
/// (clone -> predicted counts -> staging validation on the inactive color).
///
/// These tests drive the REAL migration script against a SYNTHETIC phantom.db and a
/// SQLite-backed Postgres stand-in (a <c>psql</c>-client shim identical in behaviour
/// to the one in scripts/tests/phantom-migrate-jellyfindb-to-postgres.test.sh), so
/// the count-parity assertions exercise the genuine SQLite export + load path with no
/// live PostgreSQL server. No dependency on the plugin or the patched Jellyfin tree,
/// so the project builds and runs standalone.
///
/// Asserted:
///   1. --stage loads the inactive color (phantom_dev) with per-table counts equal to
///      the stage-2 predicted counts (which equal the synthetic source counts).
///   2. The schema-version guard HARD-REFUSES a phantom.db whose PRAGMA user_version
///      does not match the expected version (mirrors PhantomDb's startup refuse and
///      the pre-v1.0 "no in-place schema migration" rule).
/// </summary>
public sealed class PhantomPostgresMigrationTests
{
    // Mirrors PhantomDb.CurrentSchemaVersion (src/.../State/PhantomDb.cs). The guard
    // in the migration script defaults to this value.
    private const int ExpectedSchemaVersion = 16;

    private static readonly bool ToolsAvailable =
        WhichExists("sqlite3") && WhichExists("bash");

    [Fact]
    public void Stage_LoadsInactiveColor_WithCountParityPerTable()
    {
        if (!ToolsAvailable)
        {
            return; // sqlite3/bash not available in this environment; nothing to exercise.
        }

        using var rig = new MigrationRig();

        // Synthetic phantom.db at the expected schema version, with known row counts
        // across a representative slice of the plugin schema shape (incl. an empty
        // table and a NULL value).
        rig.CreatePhantomDb(ExpectedSchemaVersion, """
            CREATE TABLE "plugin_meta" (Key TEXT PRIMARY KEY, Value TEXT);
            INSERT INTO "plugin_meta" VALUES ('schema_version','16'),('note','carol o''brien ran it');
            CREATE TABLE "phantom_items" (item_guid TEXT PRIMARY KEY, tmdb_id INTEGER, stub_path TEXT);
            INSERT INTO "phantom_items" VALUES ('g-1',603,'/m/a'),('g-2',1437,'/m/b'),('g-3',NULL,NULL);
            CREATE TABLE "user_prefs" (user_id TEXT, pref TEXT, PRIMARY KEY(user_id,pref));
            INSERT INTO "user_prefs" VALUES ('u-1','dark'),('u-2','light');
            CREATE TABLE "user_hidden_items" (user_id TEXT, item_guid TEXT, PRIMARY KEY(user_id,item_guid));
            """);

        var result = rig.RunMigration("--source", "phantom", "--skip-service-check", "--stage");

        Assert.True(result.ExitCode == 0,
            $"--stage exited {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");

        // The predicted-counts.tsv is the P3 stage-2 contract; every later stage must
        // reproduce it. Assert it exists and matches the real source counts.
        var predicted = rig.ReadPredictedCounts();
        Assert.NotEmpty(predicted);

        var expectedTables = new[] { "plugin_meta", "phantom_items", "user_prefs", "user_hidden_items" };
        Assert.Equal(
            expectedTables.OrderBy(t => t, StringComparer.Ordinal),
            predicted.Keys.OrderBy(t => t, StringComparer.Ordinal));

        foreach (var table in expectedTables)
        {
            var source = rig.SourceCount(table);
            Assert.Equal(source, predicted[table]);

            // Post-migration count in the inactive-color (phantom_dev) stand-in must
            // equal the predicted/source count.
            var loaded = rig.StoreCount("phantom_dev", table);
            Assert.True(loaded == source,
                $"phantom_dev.{table} post-migration count {loaded} != source/predicted {source}");
        }

        // The prod color must NOT have been written by --stage.
        Assert.Equal(-1, rig.StoreCount("phantom_prod", "plugin_meta"));

        // Staging-validation receipt written (stage-3 gate for a later --commit).
        Assert.True(rig.StagingReceiptExists(), "no .staging-validated receipt after --stage");
    }

    [Fact]
    public void SchemaVersionGuard_HardRefuses_MismatchedPhantomDb()
    {
        if (!ToolsAvailable)
        {
            return; // sqlite3/bash not available in this environment; nothing to exercise.
        }

        using var rig = new MigrationRig();

        // Same shape but at the WRONG schema version (one behind expected).
        rig.CreatePhantomDb(ExpectedSchemaVersion - 1, """
            CREATE TABLE "plugin_meta" (Key TEXT PRIMARY KEY, Value TEXT);
            INSERT INTO "plugin_meta" VALUES ('schema_version','15');
            CREATE TABLE "phantom_items" (item_guid TEXT PRIMARY KEY, tmdb_id INTEGER, stub_path TEXT);
            INSERT INTO "phantom_items" VALUES ('g-1',603,'/m/a');
            """);

        // Even a plain dry-run must refuse before generating a load set.
        var result = rig.RunMigration("--source", "phantom", "--skip-service-check");

        Assert.True(result.ExitCode != 0,
            $"migration should have REFUSED a version-{ExpectedSchemaVersion - 1} phantom.db but exited 0.\nSTDOUT:\n{result.Stdout}");
        Assert.Contains("schema version mismatch", result.Stderr, StringComparison.OrdinalIgnoreCase);

        // Guard fires at stage 1b, before any Postgres write.
        Assert.Equal(-1, rig.StoreCount("phantom_dev", "plugin_meta"));
    }

    // ----------------------------------------------------------------------
    // Rig: locates the script, builds the psql shim + synthetic source, and
    // runs the migration with the stand-in wired in.
    // ----------------------------------------------------------------------
    private sealed class MigrationRig : IDisposable
    {
        private readonly string _work;
        private readonly string _script;
        private readonly string _shim;
        private readonly string _storeDir;
        private readonly string _schemaFile;
        private readonly string _phantomDb;
        private readonly string _stagingDir;

        public MigrationRig()
        {
            _script = LocateScript();
            _work = Directory.CreateTempSubdirectory("phantom-pg-migtest-").FullName;
            _storeDir = Path.Combine(_work, "pg-store");
            Directory.CreateDirectory(_storeDir);
            _schemaFile = Path.Combine(_work, "phantom-schema.sql");
            _phantomDb = Path.Combine(_work, "phantom.db");
            _stagingDir = Path.Combine(_work, "staging");
            _shim = Path.Combine(_work, "psql-shim.sh");
            File.WriteAllText(_shim, PsqlShim);
            MakeExecutable(_shim);
        }

        public void CreatePhantomDb(int userVersion, string schemaSql)
        {
            var script = $"PRAGMA user_version={userVersion};\n{schemaSql}\n";
            RunSqlite(_phantomDb, script);

            // Schema-only dump the shim uses to seed each stand-in DB (mirrors the
            // provider/plugin having already created the destination tables).
            var dump = Run("sqlite3", new[] { _phantomDb, ".schema" }, null);
            Assert.True(dump.ExitCode == 0, $"schema dump failed: {dump.Stderr}");
            File.WriteAllText(_schemaFile, dump.Stdout);
        }

        public ProcResult RunMigration(params string[] args)
        {
            var env = new Dictionary<string, string>
            {
                ["PSQL_CMD"] = _shim,
                ["PGDUMP_CMD"] = "/does-not-exist-pgdump",
                ["SHIM_STORE"] = _storeDir,
                ["SHIM_SCHEMA"] = _schemaFile,
                ["PHANTOM_DB"] = _phantomDb,
                ["STAGING_DIR"] = _stagingDir,
                ["PG_STAGING_DB"] = "phantom_dev",
                ["PG_PROD_DB"] = "phantom_prod",
            };
            // Invoke through bash explicitly (portable regardless of the +x bit).
            var argv = new List<string> { _script };
            argv.AddRange(args);
            return Run("bash", argv, env);
        }

        public IReadOnlyDictionary<string, long> ReadPredictedCounts()
        {
            var path = Path.Combine(_stagingDir, "predicted-counts.tsv");
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return map;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    map[parts[0]] = n;
                }
            }

            return map;
        }

        public long SourceCount(string table)
        {
            var r = Run("sqlite3", new[] { _phantomDb, $"SELECT COUNT(*) FROM \"{table}\";" }, null);
            Assert.True(r.ExitCode == 0, $"source count failed for {table}: {r.Stderr}");
            return long.Parse(r.Stdout.Trim(), CultureInfo.InvariantCulture);
        }

        /// <summary>Row count in the stand-in DB, or -1 if the DB/table does not exist.</summary>
        public long StoreCount(string db, string table)
        {
            var file = Path.Combine(_storeDir, $"{db}.sqlite");
            if (!File.Exists(file))
            {
                return -1;
            }

            var r = Run("sqlite3", new[] { file, $"SELECT COUNT(*) FROM \"{table}\";" }, null);
            if (r.ExitCode != 0)
            {
                return -1;
            }

            return long.TryParse(r.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        }

        public bool StagingReceiptExists() => File.Exists(Path.Combine(_stagingDir, ".staging-validated"));

        private void RunSqlite(string dbPath, string sql)
        {
            var r = Run("sqlite3", new[] { dbPath }, null, sql);
            Assert.True(r.ExitCode == 0, $"sqlite3 create failed: {r.Stderr}");
        }

        private static string LocateScript()
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 12 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "scripts", "phantom-migrate-jellyfindb-to-postgres.sh");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            throw new FileNotFoundException(
                "Could not locate scripts/phantom-migrate-jellyfindb-to-postgres.sh by walking up from " + AppContext.BaseDirectory);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_work, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }

        // psql client stand-in, behaviourally identical to the bash regression test's
        // shim: routes each --dbname to a per-DB SQLite file under $SHIM_STORE, seeds
        // its schema from $SHIM_SCHEMA on first use, skips Postgres-only session
        // statements, and answers -tA/-c COUNT queries.
        private const string PsqlShim = """
            #!/usr/bin/env bash
            set -euo pipefail
            DB=""
            TUPLES=0
            CMD=""
            FILE=""
            while [[ $# -gt 0 ]]; do
                case "$1" in
                    --dbname=*) DB="${1#--dbname=}" ;;
                    -d)         shift; DB="${1:-}" ;;
                    --host=*|--port=*|--username=*) : ;;
                    -v)         shift ;;
                    -tA|-At|-A|-t) TUPLES=1 ;;
                    -c)         shift; CMD="${1:-}" ;;
                    -f)         shift; FILE="${1:-}" ;;
                    *)          : ;;
                esac
                shift
            done
            : "${DB:?shim: no DB name}"
            STORE_FILE="$SHIM_STORE/$DB.sqlite"
            if [[ ! -f "$STORE_FILE" && -n "${SHIM_SCHEMA:-}" && -f "$SHIM_SCHEMA" ]]; then
                sqlite3 "$STORE_FILE" < "$SHIM_SCHEMA"
            fi
            if [[ -n "$CMD" ]]; then
                SQL="$CMD"
            elif [[ -n "$FILE" ]]; then
                SQL="$(cat "$FILE")"
            else
                SQL="$(cat)"
            fi
            CLEAN="$(printf '%s\n' "$SQL" \
                | grep -viE '^[[:space:]]*SET[[:space:]]' \
                | grep -viE '^[[:space:]]*(BEGIN|COMMIT)[[:space:]]*;?[[:space:]]*$' \
                | grep -viE '^[[:space:]]*--')"
            if [[ $TUPLES -eq 1 ]]; then
                sqlite3 -noheader "$STORE_FILE" "$CLEAN"
            else
                printf '%s\n' "$CLEAN" | sqlite3 "$STORE_FILE"
            fi
            """;

        private static void MakeExecutable(string path)
        {
            try
            {
                var chmod = Run("chmod", new[] { "+x", path }, null);
                _ = chmod.ExitCode;
            }
            catch (Exception)
            {
                // non-fatal; we invoke via `bash <script>` anyway
            }
        }
    }

    private static bool WhichExists(string tool)
    {
        try
        {
            var r = Run("bash", new[] { "-c", $"command -v {tool}" }, null);
            return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.Stdout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ProcResult Run(string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        if (env is not null)
        {
            foreach (var kv in env)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {file}");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();
        }

        if (!proc.WaitForExit(120_000))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw new TimeoutException($"{file} did not exit within 120s");
        }

        stdout.Append(outTask.GetAwaiter().GetResult());
        stderr.Append(errTask.GetAwaiter().GetResult());
        return new ProcResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private readonly record struct ProcResult(int ExitCode, string Stdout, string Stderr);
}
