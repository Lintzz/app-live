using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RadminStreamApp.Models;

namespace RadminStreamApp.Services
{
    public static class FriendsService
    {
        private static string GetFilePath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadminStreamApp");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "friends.json");
        }

        public static List<Friend> LoadFriends()
        {
            try
            {
                var file = GetFilePath();
                if (File.Exists(file))
                {
                    var json = File.ReadAllText(file);
                    return JsonSerializer.Deserialize<List<Friend>>(json) ?? new List<Friend>();
                }
            }
            catch { }
            return new List<Friend>();
        }

        public static void SaveFriends(List<Friend> friends)
        {
            try
            {
                var file = GetFilePath();
                var json = JsonSerializer.Serialize(friends, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(file, json);
            }
            catch { }
        }
    }
}
