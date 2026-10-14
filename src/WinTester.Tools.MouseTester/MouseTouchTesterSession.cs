using System;
using System.Collections.Generic;

namespace WinTester.Tools.MouseTester;

// Tracks lit/unlit state for one built grid and raises Completed once every active cell has been lit.
// Pixel-to-cell hit testing and all WPF input handling (mouse capture, touch device tracking, drawing the
// trace line) live in MouseTouchTesterWindow, which is the only thing that knows about screen coordinates
// and WPF input APIs — this class only knows about (row, col) pairs.
public sealed class MouseTouchTesterSession
{
    private readonly HashSet<MouseTouchCell> _activeCells;
    private readonly HashSet<MouseTouchCell> _litCells = new();

    public int TotalCellCount => _activeCells.Count;
    public int LitCellCount => _litCells.Count;
    public bool IsComplete => _litCells.Count == _activeCells.Count;

    public event EventHandler? Completed;

    public MouseTouchTesterSession(IEnumerable<MouseTouchCell> activeCells)
    {
        _activeCells = new HashSet<MouseTouchCell>(activeCells);
    }

    // Returns true only the first time this cell transitions to lit, so the caller (which repaints the
    // cell and checks for overall completion) doesn't do either on repeated pointer samples over a cell
    // that's already lit.
    public bool TryLightCell(int row, int col)
    {
        var cell = new MouseTouchCell(row, col);
        if (!_activeCells.Contains(cell)) return false;
        if (!_litCells.Add(cell)) return false;

        if (IsComplete)
            Completed?.Invoke(this, EventArgs.Empty);

        return true;
    }
}
