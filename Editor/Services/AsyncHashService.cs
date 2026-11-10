#if UNITY_EDITOR
/// <summary>
/// Old entry point, kept only while MCBEditor still holds a field of this type: file hashes are <see cref="ContentHash"/>'s.
/// </summary>
public class AsyncHashService
{
    public static AsyncHashService Instance { get; } = new AsyncHashService();

    private AsyncHashService()
    {
    }
}
#endif
