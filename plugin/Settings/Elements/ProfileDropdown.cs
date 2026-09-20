using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;

namespace GpsSyncPlugin.Settings.Elements;

/// <summary>
/// Dropdown over Config.Current.Profiles (a runtime list, not an enum, so the
/// generic DropdownAttribute — which reflects over Enum.GetNames — can't
/// drive it). Reads the profile list fresh every time the settings dialog is
/// (re)built, so it picks up profiles added/removed/renamed since it last
/// opened; see Config.AddProfile/DeleteProfile/RefreshProfileNames, which all
/// trigger a rebuild via Plugin.RefreshSettingsUI() after mutating the list.
/// </summary>
internal class ProfileDropdownAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public ProfileDropdownAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var selectedId = (string)propertyGetter();
        var profiles = Config.Current.Profiles;

        var dropdown = new MyGuiControlCombobox(toolTip: Description);
        for (int i = 0; i < profiles.Count; i++)
            dropdown.AddItem(i, profiles[i].DisplayLabel);

        void OnItemSelect()
        {
            var key = dropdown.GetSelectedKey();
            if (key < 0 || key >= profiles.Count)
                return;

            propertySetter(profiles[(int)key].Id);

            // The Endpoint/Token/ChannelId/Nickname textboxes below this
            // dropdown were built from whatever profile was active when the
            // dialog last opened — they don't watch Config for changes, so
            // switching profiles needs an explicit rebuild to show the
            // newly-selected profile's values instead of the old one's.
            Plugin.Instance?.RefreshSettingsUI();
        }

        // Set the initial selection BEFORE subscribing ItemSelected:
        // SelectItemByIndex fires that event synchronously, and OnItemSelect
        // rebuilds the whole dialog (RefreshSettingsUI) — which recreates
        // this dropdown and calls SelectItemByIndex again, and so on.
        // Subscribing only after the initial selection means that first,
        // programmatic selection is silent; only a real user pick fires it.
        if (profiles.Count > 0)
        {
            var selectedIndex = profiles.FindIndex(p => p.Id == selectedId);
            dropdown.SelectItemByIndex(Math.Max(0, selectedIndex));
        }

        dropdown.ItemSelected += OnItemSelect;

        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        return new List<Control>()
        {
            new Control(new MyGuiControlLabel(text: label), minWidth: Control.LabelMinWidth),
            new Control(dropdown, fillFactor: 1f),
        };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(string)
    };
}
