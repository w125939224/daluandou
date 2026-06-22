using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace daluandou.Models
{
    public class GameRooms
    {
        public int Id { get; set; }

        [MaxLength(50)]
        public string? RoomOwner { get; set; }

        public bool? IsGameOver { get; set; }

        [MaxLength(50)]
        public string? RoomCode { get; set; }

        public int? MaxCount { get; set; }

        public bool IsPublic { get; set; } = true;

        [MaxLength(20)]
        public string RoomStatus { get; set; } = "Waiting";

        public int MaxPlayers { get; set; } = 4;

        public int CurrentPlayers { get; set; } = 0;

        public DateTime CreatedTime { get; set; } = DateTime.Now;

        public int? CurrentTurnPlayerId { get; set; }

        public DateTime? GameStartTime { get; set; }

        public DateTime? GameEndTime { get; set; }

        public int? WinnerId { get; set; }

        // 导航属性
        public ICollection<GamePlayer> Players { get; set; } = new List<GamePlayer>();
    }
}