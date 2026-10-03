using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace GpsSyncPlugin.Settings.Elements;

/// <summary>
/// Scrollable, read-only list — the same MyGuiControlListbox the game itself
/// uses for its own scrollable lists (e.g. jump drive waypoints) — over
/// Config.ActiveWorldsSummary's "\n"-joined lines: a status line for the
/// current world + Host Profile, then every world/server + host profile
/// combination that currently has sync on. Rows aren't interactive (nothing
/// to switch to from a status line), just scrollable once there are more
/// entries than fit.
/// </summary>
internal class WorldSyncListAttribute : Attribute, IElement
{
    private const int MinVisibleRows = 3;
    private const int MaxVisibleRows = 6;

    // MyGuiControlListbox sizes its own width from its style's ItemSize.X
    // (see MyGuiControlListbox.RefreshInternals), not from whatever width
    // Simple.LayoutControls hands its row — a plain `new MyGuiControlListbox
    // (visualStyle: Default)` renders at the Default style's native 0.25,
    // regardless of fillFactor/row width. Cloning that style and widening
    // just ItemSize.X (via the public ApplyStyle — see MyGuiControlListbox.cs)
    // is the only way to actually make it wider. 0.42 comfortably fits this
    // dialog's ~0.47-wide row content without visibly crowding the scrollbar.
    private const float ListWidth = 0.42f;

    public readonly string Label;
    public readonly string Description;

    public WorldSyncListAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var text = (string)propertyGetter() ?? "";
        var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        var listbox = new MyGuiControlListbox(visualStyle: MyGuiControlListboxStyleEnum.Default)
        {
            MultiSelect = false,
            VisibleRowsCount = Math.Max(MinVisibleRows, Math.Min(MaxVisibleRows, lines.Count == 0 ? MinVisibleRows : lines.Count)),
        };
        var wideStyle = MyGuiControlListbox.GetVisualStyle(MyGuiControlListboxStyleEnum.Default).CloneShallow();
        wideStyle.ItemSize = new VRageMath.Vector2(ListWidth, wideStyle.ItemSize.Y);
        listbox.ApplyStyle(wideStyle);
        listbox.SetToolTip(Description);

        if (lines.Count == 0)
            lines.Add("(none)");

        foreach (var line in lines)
            listbox.Add(new MyGuiControlListbox.Item(text: new StringBuilder(line)));

        // Label stacked above the listbox, instead of in its own column to the
        // left — a left column eats a fixed slice (Control.LabelMinWidth) of
        // the row before the listbox ever gets to render, and the listbox
        // doesn't stretch to reclaim it (it sizes itself from its own visual
        // style, not from whatever width Simple.LayoutControls hands it), so
        // that space was just lost. Stacking instead means the listbox starts
        // flush at the row's left edge and gets the row's full width to work
        // with. Built as one MyGuiControlParent so this is still a single
        // Control — SettingsGenerator gives each attributed property exactly
        // one row (see Simple.LayoutControls), so two rows aren't an option
        // here without touching every other element type.
        const float labelGap = 0.005f;
        var labelText = Tools.Tools.GetLabelOrDefault(name, Label);
        var labelControl = new MyGuiControlLabel(text: labelText)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP,
        };

        var containerSize = new Vector2(
            Math.Max(labelControl.Size.X, listbox.Size.X),
            labelControl.Size.Y + labelGap + listbox.Size.Y);
        var topY = -0.5f * containerSize.Y;

        // X = 0 on both — centered on the CONTAINER's own local origin, not
        // the container's edge, so this stays centered even after
        // Simple.LayoutControls stretches the container to the row's full
        // width below (fillFactor) — unrelated to the container's actual
        // final width, since a child position is relative to its parent's
        // center regardless of how wide that parent ends up.
        labelControl.Position = new Vector2(0f, topY);
        listbox.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP;
        listbox.Position = new Vector2(0f, topY + labelControl.Size.Y + labelGap);

        var container = new MyGuiControlParent()
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = containerSize,
        };
        container.Controls.Add(labelControl);
        container.Controls.Add(listbox);

        // fillFactor so this row gets the same full row width as every other
        // row (see Button.cs for why VERTICAL_TOP specifically breaks a
        // control's vertical position here — that's also why this uses the
        // default VERTICAL_CENTER instead).
        return new List<Control>()
        {
            new Control(container, fillFactor: 1f),
        };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(string)
    };
}
