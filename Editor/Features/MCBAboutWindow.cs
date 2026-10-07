#if UNITY_EDITOR
using Orbiters.Toolkit.Editor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

// About MCB: its version and license, then the third-party software it ships with and their licenses. The list is read
// from THIRD_PARTY_NOTICES.md, the file shipped next to the native libraries, so the window and the notices never differ.
public sealed class MCBAboutWindow : OrbitersAboutWindow
{
    internal const string NoticesPath = "Packages/orbiters.mcb/Editor/Plugins/Hdiff/THIRD_PARTY_NOTICES.md";

    public static void Open() => Open<MCBAboutWindow>("About My Custom Base");

    protected override Product Describe() => new Product
    {
        Name = "My Custom Base (MCB)",
        Author = "Enzo DUTRA / blackorbit",
        Version = PackageInfo.FindForAssembly(typeof(MyCustomBase).Assembly)?.version,
        LicensePath = "Packages/orbiters.mcb/LICENSE.md",
        NoticesPath = NoticesPath,
        Caption = "MCB ships with these libraries. Click one to read its license.",
        Logo = () => new MCBLogoElement(drawLogo: true, drawGlows: false),
    };
}
#endif
