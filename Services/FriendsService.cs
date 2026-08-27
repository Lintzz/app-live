using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RadminStreamApp.Models;

namespace RadminStreamApp.Services
{
    public static class FriendsService
    {
        /// <summary>
        /// Disparado quando ler ou gravar a lista falha. Antes as exceções eram engolidas em
        /// silêncio: um erro de gravação fazia o usuário perder os amigos sem nunca saber.
        /// </summary>
        public static event Action<string>? OnPersistenceError;

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
            catch (Exception ex)
            {
                OnPersistenceError?.Invoke($"Não foi possível carregar a lista de amigos: {ex.Message}");
            }
            return new List<Friend>();
        }

        /// <summary>
        /// Grava num arquivo temporário e só então substitui o definitivo: uma falha no meio
        /// da escrita não deixa o friends.json truncado.
        /// </summary>
        public static bool SaveFriends(List<Friend> friends)
        {
            var file = GetFilePath();
            var temp = file + ".tmp";

            try
            {
                var json = JsonSerializer.Serialize(friends, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(temp, json);
                File.Move(temp, file, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                OnPersistenceError?.Invoke($"Não foi possível salvar a lista de amigos: {ex.Message}");
                return false;
            }
        }
    }
}
