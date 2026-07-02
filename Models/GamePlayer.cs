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
        public int? CurrentPosition { get; set; }
        public int? Gold { get; set; }
        public int? DC { get; set; }
        public int? AC { get; set; }
        public int? HP { get; set; }
        public int? MP { get; set; }
        public int? HPMAX { get; set; }
        public int? MPMAX { get; set; }
        public string? Weapon { get; set; }
        public string? Dress { get; set; }
        public string? Helmet { get; set; }
        public string? Necklace { get; set; }
        public string? Ring { get; set; }
        public string? Armring { get; set; }

        public string? PlayerName { get; set; }

        public string? PlayerColor { get; set; }

        [ForeignKey("GameRoomId")]
        public GameRooms Room { get; set; } = null!;
        public bool IsBot { get; set; } = false;
        public int TrapTurns { get; set; } = 0;
    }
}