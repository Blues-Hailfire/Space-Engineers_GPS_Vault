using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;
using System.Text;
using VRage.Utils;
using VRageMath;

namespace GpsSyncPlugin.Settings.Elements;

/// <summary>
/// Read-only, scrollable multi-line text display — used for
/// Config.ActiveWorldsSummary, a "\n"-joined list of every world/server +
/// host profile combination that currently has sync on. There's no way to
/// show a dynamic, variable-length list of rows in this settings framework
/// (SettingsGenerator gives each attributed property/method exactly one
/// row), so this renders the whole list as one multi-line text block inside
/// a single row instead.
/// </summary>
internal class WorldSyncListAttribute : Attribute, IElement
{
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

        var multiline = new MyGuiControlMultilineText(
            size: new Vector2(0.3f, 0.08f),
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: new StringBuilder(text),
            drawScrollbarV: true,
            drawScrollbarH: false,
            selectable: true);
        multiline.SetToolTip(Description);

        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        return new List<Control>()
        {
            new Control(new MyGuiControlLabel(text: label), minWidth: Control.LabelMinWidth),
            new Control(multiline, fillFactor: 1f),
        };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(string)
    };
}
