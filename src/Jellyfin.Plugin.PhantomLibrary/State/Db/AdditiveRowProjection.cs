using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Closed, immutable row-expression vocabulary. No SQL, function names, clock,
/// arithmetic, subqueries or user-defined operators can be supplied by callers.
/// Numeric inputs accept PostgreSQL smallint/integer/bigint; outputs are bigint.
/// </summary>
public sealed class RowExpression
{
    internal enum Kind { Number, Text, Boolean }

    private RowExpression(string sql, Kind type, IEnumerable<(string Name, Kind Type)> sources)
    {
        Sql = sql;
        Type = type;
        Sources = sources.Distinct().ToArray();
    }

    internal string Sql { get; }
    internal Kind Type { get; }
    internal IReadOnlyList<(string Name, Kind Type)> Sources { get; }

    /// <summary>Reads one legacy numeric column from NEW.</summary>
    public static RowExpression NumberColumn(string name) => Column(name, Kind.Number, "int8");

    /// <summary>Reads one legacy text column from NEW using deterministic C collation.</summary>
    public static RowExpression TextColumn(string name) => Column(name, Kind.Text, "text");

    /// <summary>A literal bigint value.</summary>
    // Quote the complete signed literal before casting: -9223372036854775808::int8
    // otherwise casts its positive magnitude first and overflows on a legacy write.
    public static RowExpression Number(long value) => new($"('{value.ToString(CultureInfo.InvariantCulture)}'::pg_catalog.int8)", Kind.Number, Array.Empty<(string, Kind)>());

    /// <summary>A literal text value (never executable SQL).</summary>
    public static RowExpression Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Bound literals and disallow NUL/backslash so behavior never depends on
        // standard_conforming_strings or escape-string settings.
        if (value.Length > 256 || value.Contains('\0') || value.Contains('\\'))
        {
            throw new ArgumentException("Projection text literal is too long or contains an escape", nameof(value));
        }

        return new("('" + value.Replace("'", "''", StringComparison.Ordinal) + "'::pg_catalog.text COLLATE pg_catalog.\"C\")", Kind.Text, Array.Empty<(string, Kind)>());
    }

    /// <summary>SQL IS NULL (including unchecked legacy rows).</summary>
    public static RowExpression IsNull(RowExpression value) => new($"({value.Sql} IS NULL)", Kind.Boolean, value.Sources);

    /// <summary>SQL equality on matching primitive types.</summary>
    public static RowExpression Equal(RowExpression left, RowExpression right) => Compare(left, right, "=");

    /// <summary>SQL greater-than on matching primitive types.</summary>
    public static RowExpression GreaterThan(RowExpression left, RowExpression right) => Compare(left, right, ">");

    /// <summary>SQL boolean conjunction.</summary>
    public static RowExpression And(RowExpression left, RowExpression right)
    {
        Require(left, Kind.Boolean);
        Require(right, Kind.Boolean);
        return new($"({left.Sql} AND {right.Sql})", Kind.Boolean, left.Sources.Concat(right.Sources));
    }

    /// <summary>SQL CASE; a NULL condition selects the else branch.</summary>
    public static RowExpression Choose(RowExpression condition, RowExpression yes, RowExpression no)
    {
        Require(condition, Kind.Boolean);
        Require(no, yes.Type);
        return new($"(CASE WHEN {condition.Sql} THEN {yes.Sql} ELSE {no.Sql} END)", yes.Type, condition.Sources.Concat(yes.Sources).Concat(no.Sources));
    }

    /// <summary>First non-NULL value of matching primitive types.</summary>
    public static RowExpression Coalesce(RowExpression left, RowExpression right)
    {
        Require(right, left.Type);
        return new($"COALESCE({left.Sql}, {right.Sql})", left.Type, left.Sources.Concat(right.Sources));
    }

    /// <summary>Row-local numeric maximum; PostgreSQL ignores NULL arguments.</summary>
    public static RowExpression Greatest(RowExpression left, RowExpression right)
    {
        Require(left, Kind.Number);
        Require(right, Kind.Number);
        return new($"GREATEST({left.Sql}, {right.Sql})", Kind.Number, left.Sources.Concat(right.Sources));
    }

    private static RowExpression Column(string name, Kind type, string cast)
    {
        AdditiveRowProjection.RequireIdentifier(name);
        return new($"(NEW.{ExpandIndex.Quote(name)}::pg_catalog.{cast}{(type == Kind.Text ? " COLLATE pg_catalog.\"C\"" : string.Empty)})", type, new[] { (name, type) });
    }

    private static RowExpression Compare(RowExpression left, RowExpression right, string op)
    {
        Require(right, left.Type);
        return new($"({left.Sql} OPERATOR(pg_catalog.{op}) {right.Sql})", Kind.Boolean, left.Sources.Concat(right.Sources));
    }

    private static void Require(RowExpression value, Kind type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Type != type)
        {
            throw new ArgumentException("Projection expression types must match");
        }
    }
}

/// <summary>A new nullable bigint column with a fast, literal default and its row-local expression.</summary>
public sealed record RowProjectionColumn(string Name, long Default, RowExpression Expression);

/// <summary>
/// An immutable, versioned additive projection. Columns are created ONLY by this
/// facility, never adopted from existing tables or another expand. No feature is
/// registered by declaring this type; its owner must explicitly pass it to ApplyAsync.
/// </summary>
public sealed class AdditiveRowProjection
{
    /// <summary>Constructs and validates the entire closed definition before opening an executor session.</summary>
    public AdditiveRowProjection(string name, int version, string table, params RowProjectionColumn[] columns)
    {
        RequireIdentifier(name);
        RequireIdentifier(table);
        ArgumentNullException.ThrowIfNull(columns);
        if (version < 1 || columns.Length is < 1 or > 16)
        {
            throw new ArgumentException("Projection requires a positive version and 1..16 new columns");
        }

        Name = name;
        Table = table;
        Version = version;
        FunctionName = name + "_v" + version.ToString(CultureInfo.InvariantCulture) + "_fn";
        TriggerName = name + "_v" + version.ToString(CultureInfo.InvariantCulture) + "_tr";
        RequireIdentifier(FunctionName);
        RequireIdentifier(TriggerName);
        Columns = Array.AsReadOnly(columns.ToArray());
        foreach (var column in Columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            RequireIdentifier(column.Name);
            if (column.Expression is null || column.Expression.Type != RowExpression.Kind.Number || column.Expression.Sql.Length > 16384)
            {
                throw new ArgumentException("Projection outputs must be bounded bigint expressions");
            }
        }

        Sources = Columns.SelectMany(c => c.Expression.Sources).Distinct().OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        if (Columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != Columns.Count
            || Sources.Any(s => Columns.Any(c => c.Name == s.Name)))
        {
            throw new ArgumentException("Projection outputs must be distinct and cannot read one another");
        }
    }

    /// <summary>Logical identity, reserved across versions (a newer version is never replaced).</summary>
    public string Name { get; }
    /// <summary>Positive definition version, included in function/trigger names.</summary>
    public int Version { get; }
    /// <summary>Unqualified ordinary target table in the executor's current schema.</summary>
    public string Table { get; }
    /// <summary>Immutable snapshot of the newly introduced columns.</summary>
    public IReadOnlyList<RowProjectionColumn> Columns { get; }
    internal string FunctionName { get; }
    internal string TriggerName { get; }
    internal IReadOnlyList<(string Name, RowExpression.Kind Type)> Sources { get; }
    internal string Body => "BEGIN\n" + string.Join("\n", Columns.Select(c => $"NEW.{ExpandIndex.Quote(c.Name)} := {c.Expression.Sql};")) + "\nRETURN NEW;\nEND";
    internal string Definition => Table + "\n" + Version.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", Columns.Select(c => c.Name + ":" + c.Default.ToString(CultureInfo.InvariantCulture))) + "\n" + Body;

    internal static void RequireIdentifier(string name)
    {
        if (name is null || !Regex.IsMatch(name, "^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Projection identifiers must be unqualified lowercase ASCII PostgreSQL identifiers", nameof(name));
        }
    }
}
