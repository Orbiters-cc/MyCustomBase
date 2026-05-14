#if UNITY_EDITOR
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditorInternal;

/// <summary>
/// Centralized authentication logic for MCB editor tooling.
/// Handles Magic Sync, encrypted token storage and retrieval for
/// both production (auth.dat) and development (auth_dev.dat) environments.
/// </summary>
public static class AuthenticationService
{
    private const string AUTH_FILENAME_PROD = "auth.dat";
    private const string AUTH_FILENAME_DEV = "auth_dev.dat";

    [JsonObject(MemberSerialization.OptIn)]
    public class AuthData
    {
        [JsonProperty] public string token;
        [JsonProperty] public string user;
        [JsonProperty] public string username;
        [JsonProperty] public string avatarUrl;
    }

    /// <summary>
    /// Run Magic Sync for the currently active environment (based on MCBUtils.isDevEnvironment).
    /// </summary>
    public static Task<bool> RegisterAuth()
    {
        return RegisterAuthInternal(null);
    }

    /// <summary>
    /// Run Magic Sync for a specific environment regardless of the current global flag.
    /// </summary>
    /// <param name="isDev">True for dev auth_dev.dat, false for prod auth.dat.</param>
    public static Task<bool> RegisterAuthForEnv(bool isDev)
    {
        return RegisterAuthInternal(isDev);
    }

    private static async Task<bool> RegisterAuthInternal(bool? forceIsDev)
    {
        try
        {
            string clipboardContent = UnityEditor.EditorGUIUtility.systemCopyBuffer;
            string tokenToUse = "notoken";

            Regex tokenPattern = new Regex(@"orbit-\w{8}-\w{8}-\w{8}-\d{2}-\d{2}-\d{4}");
            if (!string.IsNullOrEmpty(clipboardContent) && tokenPattern.IsMatch(clipboardContent))
            {
                tokenToUse = clipboardContent;
                MCBLogger.Log("[MCBUtils] Found valid token pattern in clipboard");
            }

            AuthData authData = null;
            bool isValid = false;
            int retryCount = 0;
            const int maxRetries = 10;
            string req = MCBUtils.getApiUrl() + MCBUtils.TOKEN_ENDPOINT + "?token=" + tokenToUse;

            while (retryCount < maxRetries && !isValid)
            {
                try
                {
                    var response = await MCBUtils.client.GetAsync(req);
                    MCBManagedRequest.ReportHttpResponse(response, req, MCBRequestPolicy.Backend("Magic Sync authentication"));

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync();
                        authData = JsonConvert.DeserializeObject<AuthData>(jsonResponse);
                        isValid = !string.IsNullOrEmpty(authData?.token);
                        if (isValid) break;
                    }
                    else if ((int)response.StatusCode == 425)
                    {
                        retryCount++;
                        MCBLogger.Log($"[MCBUtils] Server is processing request (Status 425). Retry {retryCount}/{maxRetries} (url: {req})");
                        await Task.Delay(1000);
                    }
                    else
                    {
                        MCBLogger.LogWarning($"[MCBUtils] Authentication failed with status code {response.StatusCode} (url: {req})");
                        return false;
                    }
                }
                catch (System.Exception e)
                {
                    MCBManagedRequest.ReportException(req, e, MCBRequestPolicy.Backend("Magic Sync authentication"));
                    MCBLogger.LogError($"[MCBUtils] Error during authentication attempt {retryCount + 1}: {e.Message} (url: {req})");
                    retryCount++;
                    await Task.Delay(1000);
                }
            }

            if (isValid && authData != null)
            {
                string authPath = GetAuthFilePath(forceIsDev);
                MCBUtils.EnsureDirectoryExists(authPath);
                string authJson = JsonConvert.SerializeObject(authData);
                byte[] authBytes = Encoding.UTF8.GetBytes(authJson);
                byte[] encryptedBytes = new byte[authBytes.Length];
                byte[] key = Encoding.UTF8.GetBytes("MCBMagicSync");

                for (int i = 0; i < authBytes.Length; i++)
                {
                    encryptedBytes[i] = (byte)(authBytes[i] ^ key[i % key.Length]);
                }

                File.WriteAllBytes(authPath, encryptedBytes);
                MCBLogger.Log("[MCBUtils] Authentication data stored successfully");

                // Update user cache with info from token response (username, avatar)
                try
                {
                    if (!string.IsNullOrEmpty(authData.user) && int.TryParse(authData.user, out var parsedUserId))
                    {
                        UserService.UpdateUserInfo(parsedUserId, authData.username, authData.avatarUrl);
                    }
                }
                catch (System.Exception ex)
                {
                    MCBLogger.LogWarning($"[MCB] Could not update user info from auth response: {ex.Message}");
                }

                return true;
            }
            else
            {
                MCBLogger.LogWarning("[MCBUtils] Authentication failed after maximum retries");
                return false;
            }
        }
        catch (System.Exception e)
        {
            MCBLogger.LogError($"[MCBUtils] Error registering authentication: {e.Message}");
            return false;
        }
    }

    public static AuthData GetAuth()
    {
        return GetAuthInternal(null);
    }

    public static AuthData GetAuthForEnv(bool isDev)
    {
        return GetAuthInternal(isDev); 
    }

    private static AuthData GetAuthInternal(bool? forceIsDev)
    {
        try
        {
            string authPath = GetAuthFilePath(forceIsDev);
            if (!File.Exists(authPath)) return null;

            byte[] encryptedBytes = File.ReadAllBytes(authPath);
            byte[] key = Encoding.UTF8.GetBytes("MCBMagicSync");
            byte[] authBytes = new byte[encryptedBytes.Length];

            for (int i = 0; i < encryptedBytes.Length; i++)
            {
                authBytes[i] = (byte)(encryptedBytes[i] ^ key[i % key.Length]);
            }

            string authJson = Encoding.UTF8.GetString(authBytes);
            return JsonConvert.DeserializeObject<AuthData>(authJson);
        }
        catch (System.Exception e)
        {
            MCBLogger.LogError($"[MCBUtils] Error retrieving authentication: {e.Message}");
            return null;
        }
    }

    public static bool RemoveAuth()
    {
        return RemoveAuthInternal(null);
    }

    private static bool RemoveAuthInternal(bool? forceIsDev)
    {
        try
        {
            string authPath = GetAuthFilePath(forceIsDev);
            if (File.Exists(authPath))
            {
                File.Delete(authPath);
                MCBLogger.Log("[MCBUtils] Authentication data removed successfully");
                return true;
            }
            else
            {
                MCBLogger.Log("[MCBUtils] No authentication data found to remove");
                return false;
            }
        }
        catch (System.Exception e)
        {
            MCBLogger.LogError($"[MCBUtils] Error removing authentication data: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Compute the auth file path for the given environment.
    /// </summary>
    /// <param name="forceIsDev">
    /// True = dev auth_dev.dat, False = prod auth.dat, null = choose based on MCBUtils.isDevEnvironment.
    /// </param>
    public static string GetAuthFilePath(bool? forceIsDev = null)
    {
        bool useDev = forceIsDev ?? MCBUtils.isDevEnvironment;
        string authFolder = Path.Combine(InternalEditorUtility.unityPreferencesFolder, "MCB");
        string fileName = useDev ? AUTH_FILENAME_DEV : AUTH_FILENAME_PROD;
        return Path.Combine(authFolder, fileName);
    }
}

#endif
