using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>A deliberately small SQL grammar, not a general-purpose SQL parser.</summary>
internal sealed record ExpandIndex(string Name, string? Schema, string Table, string Keys, string Predicate, bool Unique, bool Concurrent)
{
    internal const string Identifier = @"[a-z_][a-z0-9_]{0,62}";
    private const string Literal = @"(?:-?[0-9]+(?:\.[0-9]+)?|'(?:[^'\\]|'')*'|TRUE|FALSE|NULL)";
    private const string Key = Identifier + @"(?:\s+(?:ASC|DESC))?(?:\s+NULLS\s+(?:FIRST|LAST))?";
    private const string Term = Identifier + @"\s*(?:(?:=|<>|!=|<=|>=|<|>)\s*" + Literal + @"|IS\s+(?:NOT\s+)?NULL)";
    private const string Column = Identifier + @"\s+(?:INTEGER|INT|BIGINT|SMALLINT|TEXT|REAL|BOOLEAN|DOUBLE\s+PRECISION|BYTEA|UUID|TIMESTAMPTZ)(?:\s+NOT\s+NULL)?(?:\s+DEFAULT\s+" + Literal + @")?(?:\s+PRIMARY\s+KEY)?";
    private static readonly Regex Index = new(
        @"^CREATE\s+(?<unique>UNIQUE\s+)?INDEX\s+(?<concurrent>CONCURRENTLY\s+)?IF\s+NOT\s+EXISTS\s+(?<name>" + Identifier + @")\s+ON\s+(?:(?<schema>" + Identifier + @")\.)?(?<table>" + Identifier + @")\s*(?:USING\s+btree\s*)?\((?<keys>" + Key + @"(?:\s*,\s*" + Key + @")*)\)(?:\s+WHERE\s+(?<predicate>" + Term + @"(?:\s+AND\s+" + Term + @")*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Clean(string raw)
    {
        var sql = raw.Trim();
        if (sql.EndsWith(';'))
        {
            sql = sql[..^1].TrimEnd();
        }

        // Refuse batching, comments, quoted identifiers, escapes and dollar quoting.
        // Even inside a literal these are intentionally outside this narrow grammar.
        if (sql.Contains(';') || sql.Contains("--", StringComparison.Ordinal) || sql.Contains("/*", StringComparison.Ordinal)
            || sql.Contains('"') || sql.Contains('$') || sql.Contains('\\'))
        {
            throw new NonAdditiveSchemaChangeException(raw, "ambiguous SQL: only one plain additive statement is supported");
        }

        return sql;
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "PostgreSQL folds unquoted ASCII identifiers to lowercase, not uppercase.")]
    internal static ExpandIndex? Parse(string sql)
    {
        if (!Regex.IsMatch(sql, @"^CREATE\s+(UNIQUE\s+)?INDEX\b", RegexOptions.IgnoreCase))
        {
            return null;
        }

        var match = Index.Match(sql);
        if (!match.Success)
        {
            throw new NonAdditiveSchemaChangeException(sql, "index requires plain btree columns/order and optional literal AND predicate; expressions, INCLUDE, opclasses and ambiguous names are refused");
        }

        string Id(string name) => match.Groups[name].Value.ToLowerInvariant();
        return new ExpandIndex(Id("name"), match.Groups["schema"].Success ? Id("schema") : null, Id("table"),
            match.Groups["keys"].Value, match.Groups["predicate"].Value, match.Groups["unique"].Success, match.Groups["concurrent"].Success);
    }

    internal static void ValidateTransactional(string sql)
    {
        var pk = @"PRIMARY\s+KEY\s*\(" + Identifier + @"(?:\s*,\s*" + Identifier + @")*\)";
        var table = @"^CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+" + Identifier + @"\s*\((?:" + Column + "|" + pk + @")(?:\s*,\s*(?:" + Column + "|" + pk + @"))*\)$";
        var column = @"^ALTER\s+TABLE\s+" + Identifier + @"\s+ADD\s+COLUMN\s+IF\s+NOT\s+EXISTS\s+" + Column + "$";
        if (!Regex.IsMatch(sql, table, RegexOptions.IgnoreCase) && !Regex.IsMatch(sql, column, RegexOptions.IgnoreCase))
        {
            throw new NonAdditiveSchemaChangeException(sql, "transactional expand requires a plain table or literal-default column; executable defaults/constraints and multi-actions are refused");
        }

        if (Regex.IsMatch(sql, @"^ALTER\b", RegexOptions.IgnoreCase)
            && (Regex.IsMatch(sql, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase)
                || (Regex.IsMatch(sql, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase)
                    && (!Regex.IsMatch(sql, @"\bDEFAULT\b", RegexOptions.IgnoreCase) || Regex.IsMatch(sql, @"\bDEFAULT\s+NULL$", RegexOptions.IgnoreCase)))))
        {
            throw new NonAdditiveSchemaChangeException(sql, "NOT NULL requires a non-null literal DEFAULT; adding PRIMARY KEY is not an expand");
        }
    }

    internal string Sql(string table, string name, bool concurrent) =>
        $"CREATE {(Unique ? "UNIQUE " : string.Empty)}INDEX {(concurrent ? "CONCURRENTLY " : string.Empty)}{Quote(name)} ON {table} USING btree ({Keys}){(Predicate.Length == 0 ? string.Empty : " WHERE " + Predicate)}";

    internal static string Quote(string id) => "\"" + id.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
