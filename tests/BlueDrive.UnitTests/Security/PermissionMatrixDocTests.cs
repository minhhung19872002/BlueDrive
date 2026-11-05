using System.Text;
using BlueDrive.Domain.Permissions;
using FluentAssertions;

namespace BlueDrive.UnitTests.Security;

/// <summary>
/// Spec 13.1: docs/permission-matrix.md is the table "cấp quyền × thao tác API"; this test reads it and checks every cell against the
/// built-in levels (what the engine ORs together). On a mismatch it prints the table as the code has it.
/// </summary>
public class PermissionMatrixDocTests
{
    private static string DocPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "permission-matrix.md");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("docs/permission-matrix.md");
    }

    private sealed record Row(string Operation, AccessRight Right, Dictionary<string, bool> Cells);

    private static (List<string> Levels, List<Row> Rows) Parse(string doc)
    {
        var block = doc[(doc.IndexOf("<!-- matrix:start -->", StringComparison.Ordinal) + 21)..doc.IndexOf("<!-- matrix:end -->", StringComparison.Ordinal)];
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('|')).ToList();
        static string[] Cells(string line) => line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var header = Cells(lines[0]);
        var levels = header.Skip(2).Select(h => h.Trim('`')).ToList();
        var rows = lines.Skip(2).Select(l =>
        {
            var c = Cells(l);
            var right = Enum.Parse<AccessRight>(c[1].Trim('`'));
            return new Row(c[0], right, levels.Select((lv, i) => (lv, c[i + 2] == "✓")).ToDictionary(x => x.lv, x => x.Item2));
        }).ToList();
        return (levels, rows);
    }

    [Fact]
    public void Every_cell_of_the_documented_matrix_is_what_the_levels_give()
    {
        var (levels, rows) = Parse(File.ReadAllText(DocPath()));
        levels.Should().BeEquivalentTo(SystemLevels.All.Select(l => l.Code), "every built-in level is a column");
        rows.Select(r => r.Right).Should().Contain(AccessRights.Descriptions.Keys, "every atomic right has a row");
        var wrong = new List<string>();
        foreach (var row in rows)
            foreach (var level in SystemLevels.All)
                if (row.Cells[level.Code] != level.Permissions.Has(row.Right))
                    wrong.Add($"{row.Operation} × {level.Code}: tài liệu {(row.Cells[level.Code] ? "✓" : "—")}, mã {(level.Permissions.Has(row.Right) ? "✓" : "—")}");
        wrong.Should().BeEmpty("the documented matrix must match the levels; as the code has it:\n" + Expected(rows));
    }

    private static string Expected(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Thao tác (API) | Quyền | " + string.Join(" | ", SystemLevels.All.Select(l => $"`{l.Code}`")) + " |");
        sb.AppendLine("|---|---|" + string.Concat(SystemLevels.All.Select(_ => "---|")));
        foreach (var r in rows)
            sb.AppendLine($"| {r.Operation} | `{r.Right}` | " + string.Join(" | ", SystemLevels.All.Select(l => l.Permissions.Has(r.Right) ? "✓" : "—")) + " |");
        return sb.ToString();
    }
}
