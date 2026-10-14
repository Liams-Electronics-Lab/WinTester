using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTester.Tools.MouseTester;

// Builds the "boxed X" cell layout from the reference image: a one-cell-thick border around the whole
// drawing area, plus both corner-to-corner diagonals, out of a cols x rows grid. Cols/rows are picked from
// the area's pixel size and a target cell size so cells come out close to square, then stretched by a
// fraction of a pixel per cell to fill the area exactly edge-to-edge on both axes — no leftover margin, no
// half-cell clipped at the far edge.
public static class MouseTouchGridBuilder
{
    public sealed record Layout(int Cols, int Rows, double CellWidth, double CellHeight, IReadOnlyList<MouseTouchCell> ActiveCells);

    public static Layout Build(double areaWidth, double areaHeight, double targetCellSize)
    {
        var cols = Math.Max(2, (int)Math.Round(areaWidth / targetCellSize));
        var rows = Math.Max(2, (int)Math.Round(areaHeight / targetCellSize));
        var cellWidth = areaWidth / cols;
        var cellHeight = areaHeight / rows;

        var active = new HashSet<MouseTouchCell>();
        for (var c = 0; c < cols; c++)
        {
            active.Add(new MouseTouchCell(0, c));
            active.Add(new MouseTouchCell(rows - 1, c));
        }
        for (var r = 0; r < rows; r++)
        {
            active.Add(new MouseTouchCell(r, 0));
            active.Add(new MouseTouchCell(r, cols - 1));
        }

        foreach (var cell in BresenhamLine(0, 0, cols - 1, rows - 1))
            active.Add(cell);
        foreach (var cell in BresenhamLine(cols - 1, 0, 0, rows - 1))
            active.Add(cell);

        return new Layout(cols, rows, cellWidth, cellHeight, active.ToList());
    }

    // Standard integer Bresenham line walked in (col, row) space, one cell per step with no gaps regardless
    // of the grid's aspect ratio — a stepped diagonal, matching the reference image's own hand-drawn X,
    // rather than a geometric line that could skip a cell at a shallow angle.
    private static IEnumerable<MouseTouchCell> BresenhamLine(int c0, int r0, int c1, int r1)
    {
        var dc = Math.Abs(c1 - c0);
        var sc = c0 < c1 ? 1 : -1;
        var dr = -Math.Abs(r1 - r0);
        var sr = r0 < r1 ? 1 : -1;
        var err = dc + dr;
        int c = c0, r = r0;

        while (true)
        {
            yield return new MouseTouchCell(r, c);
            if (c == c1 && r == r1) yield break;

            var e2 = 2 * err;
            if (e2 >= dr) { err += dr; c += sc; }
            if (e2 <= dc) { err += dc; r += sr; }
        }
    }
}
