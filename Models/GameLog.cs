namespace daluandou.Models
{
    public class GameLog
    {
        public int Id { get; set; }
        public int GameRoomId { get; set; }
        public int? PlayerId { get; set; }
        public string LogType { get; set; }
        public string Message { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
