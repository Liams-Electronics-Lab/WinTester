namespace WinTester.Tools.MouseTester;

// A single grid position in the boxed-X layout, identified purely by row/col — pixel placement is derived
// from these plus the layout's CellWidth/CellHeight wherever it's needed (MouseTouchTesterWindow), not
// stored here, so this stays a plain value the grid builder and session can share without pulling in WPF.
public readonly record struct MouseTouchCell(int Row, int Col);
