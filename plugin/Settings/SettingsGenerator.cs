using GpsSyncPlugin.Settings.Elements;
using GpsSyncPlugin.Settings.Layouts;
using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;


namespace GpsSyncPlugin.Settings;

internal class AttributeInfo
{
    public IElement ElementType;
    public string Name;
    public Func<object> Getter;
    public Action<object> Setter;

    // Set from a [RenderAfter("OtherMemberName")] on the same member, if
    // present — see ExtractAttributes' final reorder pass.
    public string InsertAfter;
}

/// <summary>
/// Optional marker for a [Button]/etc-attributed method: renders its row
/// immediately after the row for the named property/method, overriding the
/// default order (all property rows in declaration order, then all method
/// rows in declaration order — methods always trail every property because
/// MethodInfo/PropertyInfo MetadataTokens live in separate metadata tables
/// and can't be interleaved by sorting on the token alone).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal class RenderAfterAttribute : Attribute
{
    public readonly string Name;
    public RenderAfterAttribute(string name) => Name = name;
}

internal class SettingsGenerator
{
    public readonly string Name;

    private readonly List<AttributeInfo> attributes;
    private List<List<Control>> controls;
    public SettingsScreen Dialog { get; private set; }
    public Layout ActiveLayout { get; private set; }

    private static bool ValidateType(Type type, List<Type> typesList)
    {
        return typesList.Any(t => t.IsAssignableFrom(type));
    }

    private static Delegate GetDelegate(MethodInfo methodInfo)
    {
        // Reconstruct the type
        Type[] methodArgs = methodInfo.GetParameters().Select(p => p.ParameterType).ToArray();
        Type type = Expression.GetDelegateType(methodArgs.Concat(new[] { methodInfo.ReturnType }).ToArray());

        // Create a delegate
        return Delegate.CreateDelegate(type, null, methodInfo);
    }

    public SettingsGenerator()
    {
        attributes = ExtractAttributes();
        Name = Config.Current.Title;
        ActiveLayout = new None(()=>controls);
        Dialog = new SettingsScreen(Name, OnRecreateControls, size: ActiveLayout.SettingsPanelSize);
    }

    private List<MyGuiControlBase> OnRecreateControls()
    {
        CreateConfigControls();
        var controlsToRecreate = ActiveLayout.RecreateControls();
        ActiveLayout.LayoutControls();
        return controlsToRecreate;
    }

    public void SetLayout<T>() where T : Layout
    {
        ActiveLayout = (T)Activator.CreateInstance(typeof(T), (Func<List<List<Control>>>)(() => controls));
        Dialog.UpdateSize(ActiveLayout.SettingsPanelSize);
    }

    public void RefreshLayout()
    {
        ActiveLayout.LayoutControls();
    }

    private void CreateConfigControls()
    {
        controls = new List<List<Control>>();

        foreach (AttributeInfo info in attributes)
        {
            controls.Add(info.ElementType.GetControls(info.Name, info.Getter, info.Setter));
        }
    }

    private static List<AttributeInfo> ExtractAttributes()
    {
        var config = new List<AttributeInfo>();

        foreach (var propertyInfo in typeof(Config).GetProperties())
        {
            var name = propertyInfo.Name;
            foreach (var attribute in propertyInfo.GetCustomAttributes())
            {
                if (attribute is IElement element)
                {
                    if (!ValidateType(propertyInfo.PropertyType, element.SupportedTypes))
                    {
                        throw new Exception(
                            $"Element {element.GetType().Name} for {name} expects "
                            + $"{string.Join("/", element.SupportedTypes)} but "
                            + $"recieved {propertyInfo.PropertyType.FullName}");
                    }

                    var info = new AttributeInfo()
                    {
                        ElementType = element,
                        Name = name,
                        Getter = Getter,
                        Setter = Setter
                    };
                    config.Add(info);
                }
            }

            continue;

            object Getter() => propertyInfo.GetValue(Config.Current);
            void Setter(object value) => propertyInfo.SetValue(Config.Current, value);
        }

        foreach (var methodInfo in typeof(Config).GetMethods())
        {
            string name = methodInfo.Name;
            Delegate method = GetDelegate(methodInfo);
            var renderAfter = methodInfo.GetCustomAttribute<RenderAfterAttribute>()?.Name;

            foreach (var attribute in methodInfo.GetCustomAttributes())
            {
                if (attribute is IElement element)
                {
                    if (!ValidateType(typeof(Delegate), element.SupportedTypes))
                    {
                        throw new Exception(
                            $"Element {element.GetType().Name} for {name} expects "
                            + $"{string.Join("/", element.SupportedTypes)} but "
                            + $"recieved {typeof(Delegate).FullName}");
                    }

                    var info = new AttributeInfo()
                    {
                        ElementType = element,
                        Name = name,
                        Getter = () => method,
                        Setter = null,
                        InsertAfter = renderAfter
                    };
                    config.Add(info);
                }
            }
        }

        // Methods always land after every property above (MethodInfo and
        // PropertyInfo occupy separate metadata tables, so there's no single
        // token order spanning both) — [RenderAfter] moves a method's row
        // next to a specific property/method instead. Pull every such row
        // out first, then reinsert them back-to-front (each right after its
        // anchor) so several rows anchored to the same target end up in
        // their original relative order — doing this in one mutating pass
        // over `config` (remove-and-reinsert while iterating by index) is
        // the wrong way to do it: a removal can shift a not-yet-visited row
        // forward past the index the loop counter has already gone by,
        // silently skipping it.
        var toMove = config.Where(a => a.InsertAfter != null).ToList();
        config.RemoveAll(a => a.InsertAfter != null);

        for (int i = toMove.Count - 1; i >= 0; i--)
        {
            var moved = toMove[i];
            var anchorIndex = config.FindIndex(a => a.Name == moved.InsertAfter);
            if (anchorIndex < 0)
            {
                config.Add(moved); // anchor not found — keep the row rather than drop it
                continue;
            }
            config.Insert(anchorIndex + 1, moved);
        }

        return config;
    }
}
