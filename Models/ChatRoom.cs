using System.ComponentModel.DataAnnotations;

namespace daluandou.Models
{
    public class ChatRoom
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string RoomName { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? Password { get; set; }

        [Required]
        public string CreateUser { get; set; } = string.Empty;

        public DateTime CreateTime { get; set; }
    }
}