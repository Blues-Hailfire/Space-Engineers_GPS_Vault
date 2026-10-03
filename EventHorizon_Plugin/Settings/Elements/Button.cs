using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;
using System.Text;

namespace GpsSyncPlugin.Settings.Elements;

internal class ButtonAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public ButtonAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        var button = new MyGuiControlButton(text: new StringBuilder(label), toolTip: Description, isEllipsisEnabled: true);
        button.ButtonClicked += (_)=>((Action)propertyGetter())();

        return new List<Control>()
        {
            new Control(new MyGuiControlLabel(text: ""), fixedWidth: Control.LabelMinWidth),
            // fillFactor, not the button's own default size — without it a
            // label longer than that default just gets cut off, since only
            // the FillFactor/FixedWidth branches in Simple.LayoutControls
            // call SetMaxWidth to clip/fit text at all. Default originAlign
            // (VERTICAL_CENTER, same as every other row's control) — this row
            // previously used VERTICAL_TOP, which anchors the row's Y midpoint
            // to the control's TOP edge instead of its center, so the whole
            // control (not just its text) rendered shifted downward by half
            // its own height. Harmless mid-list, since it just overlapped the
            // next row's slightly, but for the last row (Bring Your Own Bot)
            // that overhang had nothing below it to overlap into and instead
            // ran past the scrollable content's bottom edge and got clipped —
            // "the button's bottom is cut off".
            new Control(button, fillFactor: 1f),
        };
    }
    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(Delegate)
    };
}
