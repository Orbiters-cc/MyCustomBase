#if UNITY_EDITOR
using NUnit.Framework;

public class UserServiceTests
{
    [Test]
    public void CachedUserInfoNeverInvokesCompletionInline()
    {
        const int userId = 987654321;
        bool completionCalled = false;
        try
        {
            UserService.UpdateUserInfo(userId, "Cached User", null);

            UserService.RequestUserInfo(userId, () => completionCalled = true);

            Assert.That(completionCalled, Is.False);
        }
        finally
        {
            UserService.ClearUserCache(userId);
        }
    }
}
#endif
