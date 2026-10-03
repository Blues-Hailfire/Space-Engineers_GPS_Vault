using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;

namespace GpsSyncPlugin.Settings.Elements;

/// <summary>
/// Read-only, single-line text row bound to a Func&lt;string&gt;-returning
/// method — for output (e.g. a connection test result) that should stay
/// visible in the dialog itself rather than only flash by as a transient
/// in-game chat message. Reads fresh every time the dialog (re)builds, same
/// as every other element here — see Config.GetConnectionTestResult, whose
/// caller (TestConnection) triggers a rebuild after updating the value it
/// returns.
/// </summary>
internal class StatusLabelAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public StatusLabelAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var getText = (Func<string>)propertyGetter();
        var valueLabel = new MyGuiControlLabel(text: getText() ?? "");
        valueLabel.SetToolTip(Description);

        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        return new List<Control>()
        {
            new Control(new MyGuiControlLabel(text: label), minWidth: Control.LabelMinWidth),
            new Control(valueLabel, fillFactor: 1f),
        };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(Delegate)
    };
}
