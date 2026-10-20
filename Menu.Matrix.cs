using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TCPTunnel;

public partial class Menu
{
    private sealed class MenuReveal
    {
        internal string Text; internal int Index, Left, Top, Delay, Shown, Start, End;
    }
    private readonly List<MenuReveal> menuReveals = new();
    private long menuRevealStarted;
    private bool animateNextMenuFrame;
    private ConsoleGraphic.ConsoleGeometry menuRevealGeometry;
    private string optionsFrameTitle, optionsFrameHeader, optionsFrameFooter;
    private string[] optionsFrameChoices;
    private int optionsFrameSurface, optionsFrameTheme, optionsFrameSelected;
    private bool optionsFrameCanContinue, optionsFrameGraphics;
    private string optionsFrameColors;
    private int[] optionsFramePreviewStarts, optionsFramePreviewLengths;
    private ConsoleColor?[] optionsFramePreviewColors;
    private bool[] optionsFrameHighlightOnly;

    private static string MenuColors() => String.Join("|", ConsoleTheme.SelectionStyle, ConsoleTheme.MenuText,
        ConsoleTheme.SelectionBackground, ConsoleTheme.SelectionUsesCustomColor, ConsoleTheme.SelectionCustomRgb,
        ConsoleTheme.Border, ConsoleTheme.Corners, ConsoleTheme.BackgroundMode, ConsoleTheme.BackgroundCustomRgb);

    private void PrepareOptionsFrame(string title, string[] choices, int selectedIndex)
    {
        lock (ConsoleGraphic.borderAnimationLock)
        {
            bool reuse = optionsFrameCanContinue && optionsFrameChoices?.Length == choices.Length &&
                optionsFrameTitle == title && optionsFrameHeader == optionsHeader && !mainMenuFrame &&
                optionsFrameGraphics == ConsoleGraphic.Enabled && optionsFrameSurface == ConsoleGraphic.SurfaceRevision &&
                hasRenderedMenuGeometry && ConsoleGraphic.IsConsoleGeometryCurrent(renderedMenuGeometry);
            optionsFrameCanContinue = false;
            if (!reuse)
            {
                graphic.Clear(0, 0); mainMenuFrame = false; animateNextMenuFrame = true;
                DrawMenuFrame(choices, selectedIndex);
            }
            else
            {
                TickMenuReveal(optionsFrameSelected, true);
                bool repaint = optionsFrameTheme != ConsoleGraphic.VisualThemeRevision || optionsFrameColors != MenuColors();
                if (repaint)
                {
                    graphic.Clear(0, 0); animateNextMenuFrame = false; DrawMenuFrame(choices, selectedIndex);
                }
                menuRevealGeometry = renderedMenuGeometry; menuRevealStarted = Stopwatch.GetTimestamp();
                for (int i = 0; i < choices.Length; i++)
                {
                    string before = optionsFrameChoices[i], after = choices[i];
                    bool changed = before != after;
                    bool styleChanged = OptionValue(optionsFramePreviewStarts, i, -1) != OptionValue(activePreviewStarts, i, -1) ||
                        OptionValue(optionsFramePreviewLengths, i, 0) != OptionValue(activePreviewLengths, i, 0) ||
                        OptionValue(optionsFramePreviewColors, i, (ConsoleColor?)null) != OptionValue(activePreviewColors, i, (ConsoleColor?)null) ||
                        OptionValue(optionsFrameHighlightOnly, i, false) != OptionValue(activeHighlightOnly, i, false);
                    if (!changed && !styleChanged && optionsFrameSelected == selectedIndex && !repaint) continue;
                    int start = 0, end = after.Length;
                    while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
                    while (end > start && before.Length - (after.Length - end) > start &&
                        before[before.Length - (after.Length - end) - 1] == after[end - 1]) end--;
                    bool animate = changed && end > start && ApplicationSettings.Current.MenuMatrixText && MatrixTextEffect.CanAnimate(after);
                    string shown = animate ? after.Substring(0, start) + new string(' ', end - start) + after.Substring(end) : after;
                    DrawChoiceRange(shown, i, i == selectedIndex, 0, Int32.MaxValue, before.Length);
                    if (animate) menuReveals.Add(new() { Text = after, Index = i, Start = start, End = end, Shown = start, Delay = 5 + Random.Shared.Next(21) });
                }
                if (!repaint && optionsFrameFooter != optionsFooter)
                {
                    int firstRow = top + choices.Length * menuRowSpacing + 1;
                    for (int row = firstRow; row <= ConsoleGraphic.ContentBottom; row++) ConsoleGraphic.ClearContentRow(row);
                    if (!String.IsNullOrEmpty(optionsFooter)) DrawOptionsFooter(firstRow);
                }
                DrawSnakeWarning(true);
            }
            optionsFrameTitle = title; optionsFrameHeader = optionsHeader; optionsFrameFooter = optionsFooter;
            optionsFrameChoices = (string[])choices.Clone(); optionsFrameSelected = selectedIndex;
            optionsFrameSurface = ConsoleGraphic.SurfaceRevision; optionsFrameTheme = ConsoleGraphic.VisualThemeRevision;
            optionsFrameGraphics = ConsoleGraphic.Enabled; optionsFrameColors = MenuColors();
            optionsFramePreviewStarts = activePreviewStarts == null ? null : (int[])activePreviewStarts.Clone();
            optionsFramePreviewLengths = activePreviewLengths == null ? null : (int[])activePreviewLengths.Clone();
            optionsFramePreviewColors = activePreviewColors == null ? null : (ConsoleColor?[])activePreviewColors.Clone();
            optionsFrameHighlightOnly = activeHighlightOnly == null ? null : (bool[])activeHighlightOnly.Clone();
        }
    }

    private static T OptionValue<T>(T[] values, int index, T fallback) => values != null && index < values.Length ? values[index] : fallback;
    private void ContinueOptionsFrame(int selected) { optionsFrameCanContinue = true; optionsFrameSelected = selected; }
    private static bool DrawChoiceRange(string text, int index, bool selected, int start, int length, int previousLength = 0)
        => ConsoleGraphic.DrawMenuOptionRange(text, index * menuRowSpacing, left, top, selected, start, length,
            OptionValue(activePreviewStarts, index, -1), OptionValue(activePreviewColors, index, (ConsoleColor?)null),
            OptionValue(activePreviewLengths, index, 0), OptionValue(activeHighlightOnly, index, false), previousLength);
    private void StartMenuReveal(string[] choices, int selectedIndex, ConsoleGraphic.ConsoleGeometry geometry)
    {
        bool animate = animateNextMenuFrame; animateNextMenuFrame = false;
        if (!animate || !ApplicationSettings.Current.MenuMatrixText) return;
        menuRevealGeometry = geometry; menuRevealStarted = Stopwatch.GetTimestamp();
        int baseDelay = 5;
        for (int i = 0; i < choices.Length; i++) if (MatrixTextEffect.CanAnimate(choices[i])) menuReveals.Add(new() { Text = choices[i], Index = i, End = choices[i].Length, Delay = baseDelay + Random.Shared.Next(21) });
        if (mainMenuFrame && ConsoleGraphic.Enabled)
        {
            var layout = MenuPresentation.Layout(geometry.DrawableWidth, geometry.DrawableHeight, choices.Length);
            for (int i = 0; i < layout.Header.Length; i++)
            {
                string text = layout.Header[i].Substring(0, Math.Min(layout.Header[i].Length, Math.Max(0, geometry.DrawableWidth - 2)));
                menuReveals.Add(new() { Text = text, Index = -1, End = text.Length, Left = MenuPresentation.HeaderLeft(geometry, layout.Header, i), Top = i + 1, Delay = baseDelay + Random.Shared.Next(21) });
            }
        }
        foreach (var element in menuReveals)
        {
            if (element.Index >= 0) DrawChoice(new string(' ', element.Text.Length), element.Index, element.Index == selectedIndex, false);
            else ConsoleGraphic.WriteTextRun(element.Left, element.Top, new string(' ', element.Text.Length), 0, element.Text.Length, ConsoleTheme.MenuText, ConsoleColor.Black);
        }
    }
    private void TickMenuReveal(int selectedIndex, bool finish = false)
    {
        if (menuReveals.Count == 0) return;
        if (!ConsoleGraphic.IsConsoleGeometryCurrent(menuRevealGeometry)) { menuReveals.Clear(); return; }
        int elapsed = (int)Stopwatch.GetElapsedTime(menuRevealStarted).TotalMilliseconds;
        bool active = false;
        foreach (var element in menuReveals)
        {
            int count = finish ? element.End : Math.Min(element.End, element.Start + elapsed / element.Delay);
            if (count > element.Shown)
            {
                if (element.Index >= 0 && !ConsoleGraphic.Enabled)
                    DrawChoice(element.Text.Substring(0, count) + new string(' ', element.End - count) + element.Text.Substring(element.End), element.Index, element.Index == selectedIndex, false);
                else if (element.Index >= 0) DrawChoiceRange(element.Text, element.Index, element.Index == selectedIndex, 2 + element.Shown, count - element.Shown);
                else ConsoleGraphic.WriteTextRun(element.Left + element.Shown, element.Top, element.Text, element.Shown, count - element.Shown, ConsoleTheme.MenuText, ConsoleColor.Black);
                element.Shown = count;
            }
            active |= count < element.End;
        }
        if (!active) menuReveals.Clear();
    }
    private void ShowInterfaceOptions()
    {
        int selected = 0;
        while (true)
        {
            string[] choices = { Lang.Get(TextId.DialogueInterface), Lang.Get(TextId.MenuMatrixText) + (ApplicationSettings.Current.MenuMatrixText ? " [ V ]" : " [   ]"), Lang.Get(TextId.Back) };
            var choice = ReadOptionsChoice(Lang.Get(TextId.InterfaceOptions), choices, selected, isValueOption: i => i == 1);
            if (choice.Index < 0 || choice.Index == 2) return;
            selected = choice.Index;
            if (selected == 0) ShowDialogueOptions();
            else { ApplicationSettings.Current.MenuMatrixText = !ApplicationSettings.Current.MenuMatrixText; ApplicationSettings.CaptureAndSave(); }
        }
    }
}
