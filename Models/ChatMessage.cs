using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace daluandou.Models
{
    public class ChatMessage
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public int RoomId { get; set; }

        public string SenderUsername { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime SendTime { get; set; }
    }
}