using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Orion.Windows.Sections;

/// <summary>One of the icon buttons at the header's bottom right.</summary>
internal readonly record struct HeaderAction(FontAwesomeIcon Icon, string Id, string Tip, Action Run);

/// <summary>
/// Orion's identity and navigation banner, shared by the idle and running views. Parcae's header
/// with Orion's own mark: the three stars of the belt in place of Parcae's orbiting nodes.
/// </summary>
internal static class OrionHeader
{
    private const float HeaderHeight = 92f;

    public static void Draw(string status, string detail, Vector4 accent, ReadOnlySpan<HeaderAction> actions)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, HeaderHeight * scale);
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var draw = ImGui.GetWindowDrawList();

        var left = Vector4.Lerp(Styling.CardBg, Styling.AccentViolet, 0.15f);
        var right = Vector4.Lerp(Styling.CardBg, Styling.AccentBlue, 0.07f);
        draw.AddRectFilledMultiColor(origin, end,
            ImGui.GetColorU32(left), ImGui.GetColorU32(right),
            ImGui.GetColorU32(right), ImGui.GetColorU32(left));
        draw.AddRect(origin, end, ImGui.GetColorU32(Styling.WithAlpha(Styling.AccentVioletSoft, 0.38f)),
            Styling.CardRounding, ImDrawFlags.None, 1.2f * scale);

        DrawMark(origin + new Vector2(39f, 45f) * scale, scale, draw);

        var textX = origin.X + 78f * scale;
        PutScaled("ORION", new Vector2(textX, origin.Y + 18f * scale), Styling.TextStrong, 1.62f);
        PutScaled("HUNT OPERATIONS", new Vector2(textX, origin.Y + 51f * scale), Styling.AccentVioletSoft, 0.78f);

        DrawStatusPill(end.X - 18f * scale, origin.Y + 17f * scale, status, accent, scale, draw);
        PutRight(detail, end.X - 18f * scale, origin.Y + 51f * scale, Styling.TextDim, 0.82f);

        DrawActions(actions, end.X - 18f * scale, end.Y - 30f * scale, scale);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>The belt: three stars on a slant, each breathing a third of a cycle after the last.</summary>
    private static void DrawMark(Vector2 center, float scale, ImDrawListPtr draw)
    {
        var colors = new[] { Styling.AccentBlueSoft, Styling.AccentVioletSoft, Styling.AccentAmberSoft };
        var step = new Vector2(13f, -6f) * scale;
        var first = center - step;

        draw.AddLine(first, first + step * 2f, ImGui.GetColorU32(Styling.WithAlpha(Styling.AccentVioletSoft, 0.35f)), 1.2f * scale);

        for (var i = 0; i < colors.Length; i++)
        {
            var star = first + step * i;
            var phase = (Styling.Phase(Styling.PulseOrbit) + i / 3f) % 1f;
            var breath = (MathF.Sin(phase * MathF.PI * 2f) + 1f) * 0.5f;
            draw.AddCircleFilled(star, (5.5f + 2f * breath) * scale, ImGui.GetColorU32(Styling.WithAlpha(colors[i], 0.18f)));
            draw.AddCircleFilled(star, 2.9f * scale, ImGui.GetColorU32(colors[i]));
        }
    }

    private static void DrawStatusPill(float rightX, float y, string label, Vector4 accent, float scale, ImDrawListPtr draw)
    {
        ImGui.SetWindowFontScale(0.78f);
        var textSize = ImGui.CalcTextSize(label);
        ImGui.SetWindowFontScale(1f);

        var padX = 10f * scale;
        var height = 24f * scale;
        var width = textSize.X + padX * 2f + 8f * scale;
        var origin = new Vector2(rightX - width, y);
        var end = origin + new Vector2(width, height);
        var dot = origin + new Vector2(9f * scale, height * 0.5f);

        draw.AddRectFilled(origin, end, ImGui.GetColorU32(Vector4.Lerp(Styling.CardBg, accent, 0.16f)), height * 0.5f);
        draw.AddRect(origin, end, ImGui.GetColorU32(Styling.WithAlpha(accent, 0.55f)), height * 0.5f);
        draw.AddCircleFilled(dot, 2.8f * scale, ImGui.GetColorU32(accent));

        PutScaled(label, new Vector2(dot.X + 7f * scale, y + (height - textSize.Y) * 0.5f), accent, 0.78f);
    }

    private static void DrawActions(ReadOnlySpan<HeaderAction> actions, float rightX, float y, float scale)
    {
        var button = 24f * scale;
        var gap = 5f * scale;
        var x = rightX - actions.Length * button - (actions.Length - 1) * gap;

        foreach (var action in actions)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.PushColor(ImGuiCol.Button, Styling.CardBgSoft))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Styling.CardBgHover))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, Styling.AccentViolet * 0.55f))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f * scale))
                if (ImGui.Button($"{action.Icon.ToIconString()}##orion_{action.Id}", new Vector2(button, button)))
                    action.Run();

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(action.Tip);
            x += button + gap;
        }
    }

    private static void PutScaled(string text, Vector2 position, Vector4 color, float scale)
    {
        ImGui.SetWindowFontScale(scale);
        ImGui.SetCursorScreenPos(position);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
        ImGui.SetWindowFontScale(1f);
    }

    private static void PutRight(string text, float rightX, float y, Vector4 color, float scale)
    {
        ImGui.SetWindowFontScale(scale);
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetWindowFontScale(1f);
        PutScaled(text, new Vector2(rightX - width, y), color, scale);
    }
}
