#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UIElements;

internal static class UserAvatarImage
{
    internal static void Bind(Image image, int userId, Action<Texture2D> onChanged = null)
    {
        Action apply = () => {
            Texture2D texture = UserService.GetUserAvatar(userId);
            image.image = texture;
            onChanged?.Invoke(texture);
        };
        Action requestAvatar = () => {
            apply();
            if (image.image == null) UserService.RequestUserAvatar(userId, apply);
        };
        Action refresh = () => {
            if (UserService.GetUserInfo(userId) == null) UserService.RequestUserInfo(userId, requestAvatar);
            requestAvatar();
        };
        refresh();
        // UI Toolkit automatically suspends this schedule when its element leaves the panel.
        image.schedule.Execute(refresh).Every(3000);
    }
}
#endif
