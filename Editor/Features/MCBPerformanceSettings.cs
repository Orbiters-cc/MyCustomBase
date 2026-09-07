#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.UIElements;

public static class MCBPerformanceSettings
{
    [SettingsProvider]
    public static SettingsProvider CreateProvider()
    {
        return new SettingsProvider("Preferences/MCB/Downloads", SettingsScope.User) {
            label = "MCB Downloads",
            activateHandler = (_, root) => {
                var style = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mcb/Editor/Styles/mcb-theme.uss");
                if (style != null) root.styleSheets.Add(style);
                root.style.paddingLeft = 18; root.style.paddingRight = 18; root.style.paddingTop = 12;
                var title = new Label("Faster version downloads");
                title.style.fontSize = 18; title.style.marginBottom = 12; root.Add(title);
                var automatic = new Toggle("Choose compression automatically") { value = MCBPerformance.Enabled };
                automatic.RegisterValueChangedCallback(evt => MCBPerformance.Enabled = evt.newValue); root.Add(automatic);
                var description = new Label("MCB measures compression on this computer and checks download speed in the background. It downloads 25 MB, then another 100 MB only if the first check finishes within two seconds. CPU measurements last 14 days; network measurements last one day and are updated by version downloads. Calibration pauses for real work.");
                description.style.whiteSpace = WhiteSpace.Normal; description.style.marginTop = 8; root.Add(description);
                var sharing = new Toggle("Share performance measurements") { value = MCBPerformance.ShareMeasurements };
                sharing.style.marginTop = 16;
                sharing.RegisterValueChangedCallback(evt => MCBPerformance.ShareMeasurements = evt.newValue); root.Add(sharing);
                var privacy = new Label("Shares timings, sizes, codec choices, Unity version, platform, core count, memory size and a random installation ID. Avatar contents, file paths, authentication tokens and hardware serial numbers are excluded from measurements. Reports expire after 30 days.");
                privacy.style.whiteSpace = WhiteSpace.Normal; root.Add(privacy);
                var status = new Label(MCBPerformance.Status); status.style.marginTop = 16; root.Add(status);
                var retry = new Button(MCBPerformance.Recalibrate) { text = "Measure again when idle" };
                retry.AddToClassList("mcb-button");
                retry.style.marginTop = 8; root.Add(retry);
                root.schedule.Execute(() => {
                    status.text = MCBPerformance.Enabled ? MCBPerformance.Status : "Automatic measurements are off";
                    retry.SetEnabled(MCBPerformance.Enabled);
                }).Every(1000);
            }
        };
    }
}
#endif
