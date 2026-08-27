using System.Text.Json;

namespace RadminStreamApp
{
    public class SignalingMessage
    {
        public string? Type { get; set; } // "offer", "answer", "ice", "STREAM_STOPPED"
        public string? Data { get; set; } // The SDP or ICE candidate JSON
        public string? SenderId { get; set; } // For the host to identify which client sent it

        public static string Serialize(SignalingMessage message)
        {
            return JsonSerializer.Serialize(message);
        }

        /// <summary>Devolve null para qualquer entrada inválida: os dados vêm da rede.</summary>
        public static SignalingMessage? Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<SignalingMessage>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
