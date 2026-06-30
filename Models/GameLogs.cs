namespace daluandou.Models
{
    public class GameLogs
    {
        public int Id { get; set; }
        public int GameRoomId { get; set; }
        public int? PlayerId { get; set; }
        public string? PlayerName { get; set; }
        public string? LogType { get; set; }
        public string? Message { get; set; }
        public DateTime CreatedTime { get; set; }
    }
}
