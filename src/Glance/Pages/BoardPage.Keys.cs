using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// Undo/redo, the selected node, and the keyboard.
public partial class BoardPage
{
    // ---- undo / redo ----

    /// A board's content, without the view (panning and zooming aren't undone).
    record BoardState(List<BoardItem> Items, List<BoardEdge> Edges, string Ink, List<string> Columns, string Layout, string Background);

    /// Per board: what it looks like now, and the states before and after it.
    internal sealed class History(string now)
    {
        const int Max = 100; // ponytail: whole-board snapshots; diff them if boards with big drawings make this heavy
        public string Now { get; private set; } = now;
        readonly List<string> back = [], forward = [];
        public bool CanUndo => back.Count > 0;
        public bool CanRedo => forward.Count > 0;

        /// The board as it is now; a new state is one undo step and drops what could be redone.
        public void Record(string state)
        {
            if (state == Now) return;
            back.Add(Now);
            if (back.Count > Max) back.RemoveAt(0);
            Now = state;
            forward.Clear();
        }

        /// The state to go back (or forward) to, or null when there's none.
        public string? Step(bool undo)
        {
            var (from, to) = undo ? (back, forward) : (forward, back);
            if (from.Count == 0) return null;
            to.Add(Now);
            Now = from[^1];
            from.RemoveAt(from.Count - 1);
            return Now;
        }
    }

    readonly Dictionary<Board, History> histories = [];
    DispatcherTimer? commitTimer;

    static string Take(Board b) => JsonSerializer.Serialize(new BoardState(b.Items, b.Edges, b.Ink, b.Columns, b.Layout, b.Background));

    History HistoryOf(Board b) => histories.TryGetValue(b, out var h) ? h : histories[b] = new(Take(b));

    /// After a change: a burst of them (typing, nudging) becomes one undo step once it pauses.
    void Track()
    {
        _ = HistoryOf(board); // the state before this change, if it's the first
        commitTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Commit(), Dispatcher);
        commitTimer.Stop();
        commitTimer.Start();
    }

    void Commit()
    {
        commitTimer?.Stop();
        HistoryOf(board).Record(Take(board));
    }

    bool CanUndo { get { Commit(); return HistoryOf(board).CanUndo; } }
    bool CanRedo { get { Commit(); return HistoryOf(board).CanRedo; } }

    void Undo() => Step(back: true);
    void Redo() => Step(back: false);

    void Step(bool back)
    {
        Commit();
        if (HistoryOf(board).Step(back) is not { } state) return;
        var s = JsonSerializer.Deserialize<BoardState>(state)!;
        (board.Items, board.Edges, board.Ink, board.Columns, board.Layout, board.Background) = (s.Items, s.Edges, s.Ink, s.Columns, s.Layout, s.Background);
        // The restored nodes are new objects: keep the same one selected.
        selected = selected is null ? null : board.Items.FirstOrDefault(i => i.Id == selected.Id);
        Refresh(fade: false);
    }

    // ---- the selected node: click one; keys act on it ----

    BoardItem? selected;
    readonly Dictionary<BoardItem, Action<bool>> edits = [];

    void Select(BoardItem? it)
    {
        if (it == selected) return;
        if (selected is not null && cards.TryGetValue(selected, out var was)) was.BorderBrush = Edge(selected);
        selected = it;
        if (it is not null && cards.TryGetValue(it, out var card)) card.BorderBrush = (Brush)FindResource("Accent");
    }

    // ---- copying nodes: Ctrl+C then Ctrl+V pastes the node itself while the clipboard still holds what was copied ----

    string? copiedNode;
    uint copiedAt;

    void CopyNode(BoardItem it)
    {
        Copy(it); // its text or picture, for other apps
        copiedNode = JsonSerializer.Serialize(it);
        copiedAt = Native.ClipboardSequence();
    }

    /// A copy of a node, with its own copy of its picture. Null when the picture can't be copied.
    static BoardItem? Clone(BoardItem it)
    {
        var copy = JsonSerializer.Deserialize<BoardItem>(JsonSerializer.Serialize(it))!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Locked = false;
        if (it.File is null) return copy;
        copy.File = copy.Id + System.IO.Path.GetExtension(it.File);
        try { System.IO.File.Copy(System.IO.Path.Combine(BoardStore.Files, it.File), System.IO.Path.Combine(BoardStore.Files, copy.File)); }
        catch (System.IO.IOException) { return null; }
        return copy;
    }

    void Duplicate(BoardItem it)
    {
        if (Clone(it) is not { } copy) return;
        Add(copy, new Point(it.X + 24, it.Y + 24));
        Select(copy);
    }

    /// The pointer's board point when it's over the board, else the middle of the view.
    Point Here => Surface.IsMouseOver ? ToBoard(Mouse.GetPosition(Surface)) : Middle;

    // ---- keys ----

    public static readonly (string Keys, string What)[] Shortcuts =
    [
        ("Ctrl+Z / Ctrl+Y", "Undo / redo"),
        ("Ctrl+C / Ctrl+X / Ctrl+V", "Copy, cut, paste a node (or paste text, pictures, files)"),
        ("Ctrl+D", "Duplicate the selected node"),
        ("Delete", "Delete the selected node"),
        ("Enter or F2", "Edit the selected node"),
        ("Arrow keys", "Nudge the selected node (Shift: a grid step)"),
        ("[ / ]", "Send to back / bring to front"),
        ("L", "Lock or unlock the selected node"),
        ("N", "New note at the pointer"),
        ("P / E", "Pen / eraser"),
        ("Ctrl+0 / Ctrl+1", "Zoom to 100% / fit everything"),
        ("Ctrl+= / Ctrl+-", "Zoom in / out"),
        ("Esc", "Leave the pen, or clear the selection"),
    ];

    void ShowShortcuts() => Dialog.Info("Board", "Keyboard shortcuts", string.Join("\n", Shortcuts.Select(s => $"{s.Keys}   {s.What}")));

    // Text boxes keep their own keys (their own Ctrl+Z too), so this only acts when a note isn't being typed in.
    void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var it = selected is not null && board.Items.Contains(selected) ? selected : null;
        var canvas = !Kanban;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        Action? act = (key, ctrl) switch
        {
            (Key.Z, true) when shift => Redo,
            (Key.Z, true) => Undo,
            (Key.Y, true) => Redo,
            (Key.C, true) when it is not null => () => CopyNode(it),
            (Key.X, true) when it is not null => () => { CopyNode(it); Remove(it); },
            (Key.V, true) => () =>
            {
                if (copiedNode is not null && copiedAt == Native.ClipboardSequence() && Clone(JsonSerializer.Deserialize<BoardItem>(copiedNode)!) is { } copy)
                {
                    Add(copy, canvas ? Here : new Point());
                    Select(copy);
                }
                else if (canvas) Paste(Here);
            },
            (Key.D, true) when it is not null => () => Duplicate(it),
            (Key.D0 or Key.NumPad0, true) when canvas => () => ZoomAt(ScreenMiddle, 1),
            (Key.D1 or Key.NumPad1, true) when canvas => Fit,
            (Key.OemPlus or Key.Add, true) when canvas => () => ZoomAt(ScreenMiddle, board.Zoom * 1.25),
            (Key.OemMinus or Key.Subtract, true) when canvas => () => ZoomAt(ScreenMiddle, board.Zoom / 1.25),
            (Key.Delete or Key.Back, false) when it is not null => () => Remove(it),
            (Key.Enter or Key.F2, false) when it is not null && edits.ContainsKey(it) => () => edits[it](true),
            (Key.OemOpenBrackets, false) when it is not null && canvas => () => ToBack(it),
            (Key.OemCloseBrackets, false) when it is not null && canvas => () => { ToFront(it); Changed(); },
            (Key.L, false) when it is not null && canvas => () => { it.Locked = !it.Locked; Rebuild(it); },
            (Key.N, false) when canvas => () => NewNote(Here, ""),
            (Key.P, false) when canvas => () => SetPen(PenButton.IsChecked == true ? null : InkCanvasEditingMode.Ink),
            (Key.E, false) when canvas => () => SetPen(EraserButton.IsChecked == true ? null : InkCanvasEditingMode.EraseByStroke),
            (Key.Escape, false) => () => { if (Ink.EditingMode != InkCanvasEditingMode.None) SetPen(null); else Select(null); },
            (Key.Left or Key.Right or Key.Up or Key.Down, false) when it is not null && canvas && !it.Locked => () => Nudge(it, key, shift ? Tile : 1),
            _ => null,
        };
        if (act is null) return;
        act();
        e.Handled = true;
    }

    void Nudge(BoardItem it, Key key, double by)
    {
        if (key is Key.Left or Key.Right) it.X += key == Key.Left ? -by : by;
        else it.Y += key == Key.Up ? -by : by;
        if (by == Tile) (it.X, it.Y) = (Math.Round(it.X / Tile) * Tile, Math.Round(it.Y / Tile) * Tile);
        if (cards.TryGetValue(it, out var card)) Place(it, card);
        Changed();
    }
}
