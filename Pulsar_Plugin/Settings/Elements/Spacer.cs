using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;
using VRageMath;

namespace GpsSyncPlugin.Settings.Elements;

/// <summary>
/// Blank vertical gap of a given height, for the occasional spot that needs
/// more room than the uniform small padding Simple.LayoutControls puts
/// between every row. Used on a method (so it can carry [RenderAfter]) —
/// SupportedTypes is Delegate purely to satisfy SettingsGenerator's method
/// validation, the "delegate" itself is never invoked.
/// </summary>
internal class SpacerAttribute : Attribute, IElement
{
    public readonly float Height;

    public SpacerAttribute(float height = 0.02f)
    {
        Height = height;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var blank = new MyGuiControlLabel(text: "", size: new Vector2(0.01f, Height));
        return new List<Control>() { new Control(blank) };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(Delegate)
    };
}
