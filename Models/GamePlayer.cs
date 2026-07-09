using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace daluandou.Models
{
    public class GamePlayer
    {
        public int Id { get; set; }
        public string? UserId { get; set; }
        public int? GameRoomId { get; set; }
        public string? GameRoom { get; set; }
        public int CurrentPosition { get; set; }
        public int Gold { get; set; } = 0;
        public int DC { get; set; } = 0;
        public int AC { get; set; } = 0;
        public int HP { get; set; } = 100;
        public int MP { get; set; } = 100;
        public int HPMAX { get; set; } = 100;
        public int MPMAX { get; set; } = 100;
        public string? Weapon { get; set; } = "无";
        public string? Dress { get; set; } = "无";
        public string? Helmet { get; set; } = "无";
        public string? Necklace { get; set; } = "无";
        public string? Ring { get; set; } = "无";
        public string? Armring { get; set; } = "无";

        public string? PlayerName { get; set; }

        public string? PlayerColor { get; set; }

        [ForeignKey("GameRoomId")]
        public GameRooms Room { get; set; } = null!;
        public bool IsBot { get; set; } = false;
        public int TrapTurns { get; set; } = 0;
        public string? LearnedMagicIds { get; set; }
    }
}