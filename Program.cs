using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace UnlockAllFilesOnACCproject
{
    class Program
    {
        // Fill in your credentials and project info
        private const string ClientId = ""; //from APS app, ~32 digits
        private const string ClientSecret = ""; //from APS app, ~16 digits
        private const string AccountId = "b."; //from browser address line ACC account area, GUID 8-4-4-4-10 digits, add "b." in front for ACC
        private const string ProjectId = "b."; //from browser address line ACC project area, GUID 8-4-4-4-10 digits, add "b." in front for ACC
        private const string UserId = ""; //oxygen ID of the admin, ~12 digits, e.g. look up in ACC Insight data connector (admin_users.csv from downloaded zip)

        private static readonly HttpClient httpClient = new HttpClient();

        static async Task Main(string[] args)
        {
            // 1. Authenticate and get access token
            var accessToken = await GetAccessTokenAsync();

            // 2. Get top folders in the project
            var topFolders = await GetTopFoldersAsync(accessToken);

            var lockedFiles = new List<(string itemId, string itemName)>();

            // 3. Recursively process folders to find locked files
            foreach (var folder in topFolders)
            {
                await FindLockedFilesInFolderAsync(accessToken, folder.id, lockedFiles);
            }

            Console.WriteLine($"Found {lockedFiles.Count} locked files.");

            // 4. Unlock files
            foreach (var file in lockedFiles)
            {
                Console.WriteLine($"Unlocking: {file.itemName} ({file.itemId})");
                await UnlockFileAsync(accessToken, file.itemId, UserId);
            }
        }

        // Get Forge access token using client credentials
        static async Task<string> GetAccessTokenAsync()
        {
            var authUrl = "https://developer.api.autodesk.com/authentication/v2/token";
            var dict = new Dictionary<string, string>
            {
                {"client_id", ClientId},
                {"client_secret", ClientSecret},
                {"grant_type", "client_credentials"},
                {"scope", "data:read data:write account:read"}
            };
            var req = new HttpRequestMessage(HttpMethod.Post, authUrl)
            {
                Content = new FormUrlEncodedContent(dict)
            };
            var resp = await httpClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString();
        }

        // Get top folders in the project
        static async Task<List<(string id, string name)>> GetTopFoldersAsync(string accessToken)
        {
            var url = $"https://developer.api.autodesk.com/project/v1/hubs/{AccountId}/projects/{ProjectId}/topFolders";
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await httpClient.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var list = new List<(string id, string name)>();
            foreach (var folder in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = folder.GetProperty("id").GetString();
                var name = folder.GetProperty("attributes").GetProperty("name").GetString();
                list.Add((id, name));
            }
            return list;
        }

        // Recursively find locked files in a folder
        static async Task FindLockedFilesInFolderAsync(string accessToken, string folderId, List<(string itemId, string itemName)> lockedFiles)
        {
            var url = $"https://developer.api.autodesk.com/data/v1/projects/{ProjectId}/folders/{folderId}/contents";

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await httpClient.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                if (type == "folders")
                {
                    // Recurse into subfolders
                    var subfolderId = item.GetProperty("id").GetString();
                    await FindLockedFilesInFolderAsync(accessToken, subfolderId, lockedFiles);
                }
                else if (type == "items")
                {
                    //Console.WriteLine(item.ToString());
                    var itemId = item.GetProperty("id").GetString();
                    var itemName = item.GetProperty("attributes").GetProperty("displayName").GetString();

                    // Check if item is locked
                    if (await IsFileLockedAsync(accessToken, itemId))
                        lockedFiles.Add((itemId, itemName));
                }
            }
        }

        // Checks if the file is locked by reading attributes.reserved from the item details endpoint
        static async Task<bool> IsFileLockedAsync(string accessToken, string itemId)
        {
            var url = $"https://developer.api.autodesk.com/data/v1/projects/{ProjectId}/items/{itemId}";
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            try
            {
                var resp = await httpClient.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                    return false; // Treat any error as not locked

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // "attributes" should contain "reserved" flag
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("attributes", out var attributes) &&
                    attributes.TryGetProperty("reserved", out var reservedProp))
                {
                    return reservedProp.GetBoolean();
                }

                return false;
            }
            catch (HttpRequestException)
            {
                // Any error means treat as not locked, do not throw!
                return false;
            }
        }

        // Unlock a file (delete the lock) only if it's locked, with user context in request header
        static async Task UnlockFileAsync(string accessToken, string itemId, string userId)
        {
            // Check if the file is locked before attempting to unlock
            /*if (!await IsFileLockedAsync(accessToken, itemId))
            {
                Console.WriteLine($"File {itemId} is not locked.");
                return;
            }*/

            var url = $"https://developer.api.autodesk.com/data/v1/projects/{ProjectId}/items/{itemId}";
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (httpClient.DefaultRequestHeaders.Contains("x-user-id"))
                httpClient.DefaultRequestHeaders.Remove("x-user-id");
            httpClient.DefaultRequestHeaders.Add("x-user-id", userId);

            // Prepare PATCH payload to unlock (reserved: false)

            var payload = new
            {
                jsonapi = new
                {
                    version = "1.0"
                },
                data = new
                {
                    type = "items",
                    id = itemId,
                    attributes = new
                    {
                        reserved = false
                    }
                }
            };

            var jsonPayload = JsonSerializer.Serialize(payload);

            // Log the payload
            //Console.WriteLine("Payload:");
            //Console.WriteLine(jsonPayload);

            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            //content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.api+json");

            var resp = await httpClient.PatchAsync(url, content);
            if (resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Unlocked file: {itemId}");
            }
            else
            {
                Console.WriteLine($"Failed to unlock file: {itemId} ({resp.StatusCode})");
                var errorDetails = await resp.Content.ReadAsStringAsync();
                var reasonPhrase = resp.ReasonPhrase;
                Console.WriteLine($"Error details: {errorDetails}");
            }
        }
    }
}